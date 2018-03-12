﻿namespace GraphView.Transaction
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Data.Entity;
    using System.Threading.Tasks;
    using GraphView.GraphViewDBPortal;
    using Newtonsoft.Json.Linq;
    using System.Runtime.Serialization;

    public class Transaction
    {
        /// <summary>
        /// Data store for loggingl
        /// </summary>
        private readonly LogStore logStore;

        /// <summary>
        /// Version Db for concurrency control
        /// </summary>
        private readonly VersionDb versionDb;

        /// <summary>
        /// Transaction table, keeping track of each transcation's status 
        /// </summary>
        private readonly TransactionTable txTable;

        /// <summary>
        /// Transaction id assigned to this transaction
        /// </summary>
        private readonly long txId;

        /// <summary>
        /// Begin timestamp assigned to this transaction
        /// </summary>
        private readonly long beginTimestamp;


        /// <summary>
        /// End timestamp assigned to this transaction
        /// </summary>
        private long endTimestamp;

        /// <summary>
        /// The status of this transaction.
        /// </summary>
        private TxStatus txStatus;

        /// <summary>
        /// Read set, using for checking visibility of the versions read.
        /// For every read operation, add the recordId, the begin and the end timestamp of the version we read to the readSet.
        /// </summary>
        private readonly Dictionary<string, HashSet<ReadSetEntry>> readSet;

        /// <summary>
        /// Scan set, using for checking phantoms.
        /// To do a index scan, a transaction T specifies an index I, a predicate P, abd a logical read time RT.
        /// We only have one index (recordId) currently, just add the recordId and the readTimestamp to the scanSet.
        /// </summary>
        private readonly Dictionary<string, HashSet<ScanSetEntry>> scanSet;

        /// <summary>
        /// Write set, using for
        /// 1) logging new versions during commit
        /// 2) updating the old and new versions' timestamps during commit
        /// 3) locating old versions for garbage collection
        /// Add the versions updated (old and new), versions deleted (old), and versions inserted (new) to the writeSet.
        /// </summary>
        private readonly Dictionary<string, HashSet<WriteSetEntry>> writeSet;

        private readonly ITxSequenceGenerator seqGenerator;

        public long BeginTimestamp
        {
            get
            {
                return this.beginTimestamp;
            }
        }

        public long TxId
        {
            get
            {
                return this.txId;
            }
        }

        public Transaction(long txId, long beginTimestamp, LogStore logStore, VersionDb versionDb, TransactionTable txTable)
        {
            this.txId = txId;
            this.beginTimestamp = beginTimestamp;
            this.logStore = logStore;
            this.versionDb = versionDb;
            this.txTable = txTable;

            this.endTimestamp = long.MinValue;
            this.txStatus = TxStatus.Active;

            this.readSet = new Dictionary<string, HashSet<ReadSetEntry>>();
            this.scanSet = new Dictionary<string, HashSet<ScanSetEntry>>();
            this.writeSet = new Dictionary<string, HashSet<WriteSetEntry>>();

            this.txTable.InsertNewTx(this.txId, this.beginTimestamp);
        }

        public Transaction(
            LogStore logStore, 
            VersionDb versionDb, 
            TransactionTable txTable, 
            ITxSequenceGenerator seqGenerator)
        {
            this.seqGenerator = seqGenerator;

            long sequenceNumber = this.seqGenerator.NextSequenceNumber();
            this.txId = sequenceNumber;
            this.beginTimestamp = sequenceNumber;
            this.logStore = logStore;
            this.versionDb = versionDb;
            this.txTable = txTable;

            this.endTimestamp = long.MinValue;
            this.txStatus = TxStatus.Active;

            this.readSet = new Dictionary<string, HashSet<ReadSetEntry>>();
            this.scanSet = new Dictionary<string, HashSet<ScanSetEntry>>();
            this.writeSet = new Dictionary<string, HashSet<WriteSetEntry>>();

            this.txTable.InsertNewTx(this.txId, this.beginTimestamp);
        }

        // The special constructor is used to deserialize values.
        public Transaction(SerializationInfo info, StreamingContext context)
        {
            this.logStore = (LogStore) info.GetValue("logStore", typeof(LogStore));
            this.versionDb = (VersionDb) info.GetValue("versionDb", typeof(VersionDb));
            
            this.txId = (long) info.GetValue("txId", typeof(long));
            this.beginTimestamp = (long) info.GetValue("beginTimestamp", typeof(long));
            this.endTimestamp = (long) info.GetValue("endTimestamp", typeof(long));
            this.txStatus = (TxStatus) info.GetValue("txStatus", typeof(TxStatus));

            this.readSet = (Dictionary<string, HashSet<ReadSetEntry>>)info.
                GetValue("readSet", typeof(Dictionary<string, HashSet<ReadSetEntry>>));
            this.scanSet = (Dictionary<string, HashSet<ScanSetEntry>>)info.
                GetValue("scanSet", typeof(Dictionary<string, HashSet<ScanSetEntry>>));
            this.writeSet = (Dictionary<string, HashSet<WriteSetEntry>>)info.
                GetValue("writeSet", typeof(Dictionary<string, HashSet<WriteSetEntry>>));

            this.seqGenerator = (ITxSequenceGenerator) info.
                GetValue("seqGenerator", typeof(ITxSequenceGenerator));
        }

        public void AddReadSet(string tableId, object recordKey, long beginTimestamp)
        {
            if (!this.readSet.ContainsKey(tableId))
            {
                this.readSet.Add(tableId, new HashSet<ReadSetEntry>());
            }
            this.readSet[tableId].Add(new ReadSetEntry(recordKey, beginTimestamp));
        }

        public void AddScanSet(string tableId, object recordKey, long readTimestamp, bool hasVisibleVersion)
        {
            if (!this.scanSet.ContainsKey(tableId))
            {
                this.scanSet.Add(tableId, new HashSet<ScanSetEntry>());
            }
            this.scanSet[tableId].Add(new ScanSetEntry(recordKey, readTimestamp, hasVisibleVersion));
        }

        public void AddWriteSet(string tableId, object recordKey, long beginTimestamp, bool isOld)
        {
            if (!this.writeSet.ContainsKey(tableId))
            {
                this.writeSet.Add(tableId, new HashSet<WriteSetEntry>());
            }
            this.writeSet[tableId].Add(new WriteSetEntry(recordKey, beginTimestamp, isOld));
        }

        /// <summary>
        /// Insert a new record.
        /// (1) Add the scan info to the scan set
        /// (2) Find whether the record already exist.
        /// (3) Insert, add info to write set, or, abort.
        /// </summary>
        public void InsertJson(string tableId, object recordKey, JObject record)
        {
            if (!this.versionDb.InsertVersion(tableId, recordKey, record, this.txId, this.beginTimestamp))
            {
                //insert failed, because there is already a visible version with the same versionKey
                this.Abort();
                throw new Exception($"Insert failed. Version with recordKey '{recordKey}' already exist.");
            }

            //insert successfully
            //add the scan info to scanSet (check version phatom)
            this.AddScanSet(tableId, recordKey, this.beginTimestamp, false);
            //add the write info to writeSet
            this.AddWriteSet(tableId, recordKey, this.txId, false);
        }

        /// <summary>
        /// Read the legal record.
        /// (1) Add the scan info to the scanSet.
        /// (2) Try to get the legal version from versionTable.
        /// (3) Add the read info to the readSet, or, abort.
        /// </summary>
        public JObject ReadJson(string tableId, object recordKey)
        {
            VersionEntry version = this.versionDb.ReadVersion(tableId, recordKey, this.beginTimestamp);

            if (version == null)
            {
                //can not find the legal version to read
                this.AddScanSet(tableId, recordKey, this.beginTimestamp, false);
                return null;
            }

            //read successfully
            this.AddScanSet(tableId, recordKey, this.beginTimestamp, true);
            this.AddReadSet(tableId, recordKey, version.BeginTimestamp);
            return version.JsonRecord;
        }

        /// <summary>
        /// Update a record.
        /// (1) Add the scan info to the scanSet.
        /// (2) Try to altomically set the old version's endTimestamp, create and insert a new version
        /// (3) Add the write info (old and new) to the writeSet.
        /// </summary>
        public void UpdateJson(string tableId, object recordKey, JObject record)
        {
            VersionEntry oldVersion = null;
            VersionEntry newVersion = null;
            if (!this.versionDb.UpdateVersion(tableId, recordKey, record, this.txId, this.beginTimestamp, out oldVersion, out newVersion))
            {
                //update failed, two situation:
                this.Abort();
                if (oldVersion != null)
                {
                    throw new Exception($"Update failed. Conflict on modifying the version with recordKey '{recordKey}'.");
                }
                else
                {
                    throw new Exception($"Update failed. Can not find the legal version with recordKey '{recordKey}', or" +
                                        $" the version is only visible but not updatable.");
                }
            }
            else
            {
                //update successfully, two situation:
                //case 1: the UpdateVersion() method change the old version and creat a new version,
                //insert both the old and new write info to the writeSet.
                if (oldVersion != null)
                {
                    this.AddWriteSet(tableId, recordKey, oldVersion.BeginTimestamp, true);
                    this.AddWriteSet(tableId, recordKey, this.txId, false);
                }
                //case 2: the UpdateVersion() method perform change on the new version directly,
                //do nothing.
            }
        }

        /// <summary>
        /// Delete a record.
        /// (1) Add the scan info to the scanSet.
        /// (2) Try to delete a version from versionDb.
        /// (3) If success, add the write info to the writeSet.
        /// </summary>
        public void DeleteJson(string tableId, object recordKey)
        {
            VersionEntry deletedVersion = null;
            if (!this.versionDb.DeleteVersion(tableId, recordKey, this.txId, this.beginTimestamp, out deletedVersion))
            {
                this.Abort();
                if (deletedVersion != null)
                {
                    throw new Exception($"Delete failed. Conflict on modifying the version with recordKey '{recordKey}'.");
                }
                else
                {
                    throw new Exception($"Delete failed. Can not find the legal version with recordKey '{recordKey}', or" +
                                        $" the version is only visible but not deletable.");
                }
            }
            //delete successfully
            else
            {
                if (deletedVersion != null)
                {
                    this.AddWriteSet(tableId, recordKey, deletedVersion.BeginTimestamp, true);
                }
            }
        }

        public JObject ReadJson(string recordId, JObject valueFromDataStore)
        {
            throw new NotImplementedException();
        }

        public IEnumerable<Tuple<string, JObject>> ReadJson(IEnumerable<string> ridList)
        {
            throw new NotImplementedException();
        }

        public IEnumerable<Tuple<string, JObject>> ReadJson(IEnumerable<Tuple<string, JObject>> recordList)
        {
            throw new NotImplementedException();
        }

        public IEnumerable<JObject> ReadJson(RecordQuery recordQuery)
        {
            throw new NotImplementedException();
        }

        public string ReadJsonString(string recordId)
        {
            throw new NotImplementedException();
        }

        public byte[] ReadBytes(string recordId)
        {
            throw new NotImplementedException();
        }
        
        public void WriteJson(JObject record)
        {
            throw new NotImplementedException();
        }

        public void WrilteJson(IList<JObject> recordList)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// The transaction scans its ReadSet and for each version read, 
        /// checks whether the version is still visible at the end of the transaction.
        /// </summary>
        internal void ReadValidation()
        {
            foreach (string tableId in this.readSet.Keys)
            {
                foreach (ReadSetEntry readEntry in this.readSet[tableId])
                {
                    if (!this.versionDb.CheckReadVisibility(tableId, readEntry.Key, readEntry.BeginTimestamp,
                        this.endTimestamp, this.txId, this.txTable))
                    {
                        throw new Exception($"Read validation failed. " +
                                            $"The version with tableId {tableId} recordKey {readEntry.Key} is not visible.");
                    }
                }
            }
        }

        /// <summary>
        /// The transaction walks its ScanSet and repeats each scan,
        /// looking for versions that came into existence during T’s lifetime and are visible as of the end of the transaction.
        /// </summary>
        internal void PhantomValidation()
        {
            foreach (string tableId in this.scanSet.Keys)
            {
                foreach (ScanSetEntry scanEntry in this.scanSet[tableId])
                {
                    if (!this.versionDb.CheckPhantom(
                        tableId, 
                        scanEntry.Key, 
                        scanEntry.ReadTimestamp,
                        this.endTimestamp,
                        this.txId,
                        this.txTable))
                    {
                        throw new Exception($"Check phantom failed. " +
                                            $"Find new version with tableId {tableId} recordKey {scanEntry.Key}.");
                    }
                }
            }
        }

        /// <summary>
        /// Write changes to LogStore.
        /// </summary>
        internal void WriteChangestoLog()
        {
            throw new NotImplementedException();
        }

        public void Commit()
        {
            if (this.seqGenerator == null)
            {
                throw new Exception("Transaction cannot commit: transaction sequence number generator is not provided.");
            }

            long endTimestamp = -1;
            try
            {
                endTimestamp = this.seqGenerator.NextSequenceNumber();
            }
            catch (Exception e)
            {
                throw new Exception("Failed to acquire the commit timestamp.", e);
            }

            this.Commit(endTimestamp);
        }

        /// <summary>
        /// After complete all its normal processing, the transaction first acquires a end timestamp, then,
        /// checks visibility of the versions read, checks for phantoms,
        /// writes the new versions it created, and info about the deleted version to a persistent log,
        /// propagates its end timestamp to the Begin and End fields of new and old versions, respectively, listed in its writeSet.
        /// </summary>
        public void Commit(long endTimestamp)
        {
            this.endTimestamp = endTimestamp;
            //Read validation
            try
            {
                this.ReadValidation();
            }
            catch (Exception e)
            {
                this.Abort();
                throw e;
            }
            //Check phantom
            try
            {
                this.PhantomValidation();
            }
            catch (Exception e)
            {
                this.Abort();
                throw e;
            }
            //logging
            this.WriteChangestoLog();
            //change the transaction's status
            this.txStatus = TxStatus.Committed;
            this.txTable.UpdateTxEndTimestampByTxId(this.txId, this.endTimestamp);
            this.txTable.UpdateTxStatusByTxId(this.txId, TxStatus.Committed);
            //propagates endtimestamp to versionTable
            foreach (string tableId in this.writeSet.Keys)
            {
                foreach (WriteSetEntry writeSetEntry in this.writeSet[tableId])
                {
                    this.versionDb.UpdateCommittedVersionTimestamp(tableId, writeSetEntry.Key, this.txId,
                        this.endTimestamp);
                }
            }
        }

        /// <summary>
        /// Abort this transaction.
        /// </summary>
        public void Abort()
        {
            //change the transaction's status
            this.txStatus = TxStatus.Aborted;
            this.txTable.UpdateTxStatusByTxId(this.txId, TxStatus.Aborted);
            //update all changed version's timestamp
            foreach (string tableId in this.writeSet.Keys)
            {
                foreach (WriteSetEntry writeSetEntry in this.writeSet[tableId])
                {
                    this.versionDb.UpdateAbortedVersionTimestamp(tableId, writeSetEntry.Key, this.txId);
                }
            }
        }

        /// <summary>
        /// Serializer Interface
        /// TODO: ignore the txTable since it will be dropped from the transaction
        /// </summary>
        /// <param name="info"></param>
        /// <param name="context"></param>
        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("logStore", this.logStore, typeof(LogStore));
            info.AddValue("versionDb", this.versionDb, typeof(VersionDb));
            // info.AddValue("txTableId", this.txTable, typeof(TransactionTable));
            info.AddValue("txId", this.txId, typeof(long));
            info.AddValue("beginTimestamp", this.beginTimestamp, typeof(long));
            info.AddValue("endTimestamp", this.endTimestamp, typeof(long));
            info.AddValue("txStatus", this.txStatus, typeof(TxStatus));

            info.AddValue("readSet", this.readSet, typeof(Dictionary<string, HashSet<ReadSetEntry>>));
            info.AddValue("scanSet", this.scanSet, typeof(Dictionary<string, HashSet<ScanSetEntry>>));
            info.AddValue("writeSet", this.writeSet, typeof(Dictionary<string, HashSet<WriteSetEntry>>));

            info.AddValue("seqGenerator", this.seqGenerator, typeof(ITxSequenceGenerator));
        }
    }
}
