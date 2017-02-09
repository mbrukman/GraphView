﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace GraphView
{
    internal class GremlinUnionVariable: GremlinTableVariable
    {
        public static GremlinUnionVariable Create(List<GremlinToSqlContext> unionContextList)
        {
            if (GremlinUtil.IsTheSameOutputType(unionContextList))
            {
                switch (unionContextList.First().PivotVariable.GetVariableType())
                {
                    case GremlinVariableType.Vertex:
                        return new GremlinUnionVertexVariable(unionContextList);
                    case GremlinVariableType.Edge:
                        return new GremlinUnionEdgeVariable(unionContextList);
                    case GremlinVariableType.Scalar:
                        return new GremlinUnionScalarVariable(unionContextList);
                    case GremlinVariableType.NULL:
                        return new GremlinUnionNullVariable(unionContextList);
                }
            }
            return new GremlinUnionVariable(unionContextList);
        }

        public List<GremlinToSqlContext> UnionContextList { get; set; }

        public GremlinUnionVariable(List<GremlinToSqlContext> unionContextList, GremlinVariableType variableType = GremlinVariableType.Table)
            : base(variableType)
        {
            UnionContextList = unionContextList;
        }

        internal override void Populate(string property)
        {
            if (ProjectedProperties.Contains(property)) return;

            base.Populate(property);
            foreach (var context in UnionContextList)
            {
                context.Populate(property);
            }
        }

        internal override void PopulateGremlinPath()
        {
            foreach (var context in UnionContextList)
            {
                context.PopulateGremlinPath();
            }
        }

        internal override List<GremlinVariable> FetchAllVariablesInCurrAndChildContext()
        {
            List<GremlinVariable> variableList = new List<GremlinVariable>();
            foreach (var context in UnionContextList)
            {
                var subContextVariableList = context.FetchAllVariablesInCurrAndChildContext();
                if (subContextVariableList != null)
                {
                    variableList.AddRange(subContextVariableList);
                }
            }
            return variableList;
        }

        internal override List<GremlinVariable> PopulateAllTaggedVariable(string label)
        {
            GremlinBranchVariable branchVariable = new GremlinBranchVariable(label, this);
            foreach (var context in UnionContextList)
            {
                var variableList = context.SelectCurrentAndChildVariable(label);
                branchVariable.BrachVariableList.Add(variableList);
            }
            return new List<GremlinVariable>() {branchVariable};
        }

        internal override bool ContainsLabel(string label)
        {
            if (base.ContainsLabel(label)) return true;
            foreach (var context in UnionContextList)
            {
                foreach (var variable in context.VariableList)
                {
                    if (variable.ContainsLabel(label))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public override WTableReference ToTableReference()
        {
            List<WScalarExpression> parameters = new List<WScalarExpression>();

            //if (projectProperties.Count == 0)
            //{
            //    Populate(UnionContextList.First().PivotVariable.DefaultVariableProperty().VariableProperty);
            //}
            foreach (var context in UnionContextList)
            {
                parameters.Add(SqlUtil.GetScalarSubquery(context.ToSelectQueryBlock(ProjectedProperties)));
            }
            var secondTableRef = SqlUtil.GetFunctionTableReference(GremlinKeyword.func.Union, parameters, this, VariableName);

            return SqlUtil.GetCrossApplyTableReference(null, secondTableRef);
        }
    }

    internal class GremlinUnionVertexVariable : GremlinUnionVariable
    {
        public GremlinUnionVertexVariable(List<GremlinToSqlContext> unionContextList)
            : base(unionContextList, GremlinVariableType.Vertex)
        {
        }

        internal override void Both(GremlinToSqlContext currentContext, List<string> edgeLabels)
        {
            currentContext.Both(this, edgeLabels);
        }

        internal override void BothE(GremlinToSqlContext currentContext, List<string> edgeLabels)
        {
            currentContext.BothE(this, edgeLabels);
        }

        internal override void BothV(GremlinToSqlContext currentContext)
        {
            currentContext.BothV(this);
        }

        internal override void In(GremlinToSqlContext currentContext, List<string> edgeLabels)
        {
            currentContext.In(this, edgeLabels);
        }

        internal override void InE(GremlinToSqlContext currentContext, List<string> edgeLabels)
        {
            currentContext.InE(this, edgeLabels);
        }

        internal override void Out(GremlinToSqlContext currentContext, List<string> edgeLabels)
        {
            currentContext.Out(this, edgeLabels);
        }

        internal override void OutE(GremlinToSqlContext currentContext, List<string> edgeLabels)
        {
            currentContext.OutE(this, edgeLabels);
        }
    }

    internal class GremlinUnionEdgeVariable : GremlinUnionVariable
    {
        public GremlinUnionEdgeVariable(List<GremlinToSqlContext> unionContextList)
            : base(unionContextList, GremlinVariableType.Edge)
        {
        }

        internal override WEdgeType GetEdgeType()
        {
            if (UnionContextList.Count <= 1) return (UnionContextList.First().PivotVariable as GremlinEdgeTableVariable).EdgeType;
            for (var i = 1; i < UnionContextList.Count; i++)
            {
                var isSameType = UnionContextList[i - 1].PivotVariable.GetEdgeType()
                                  == UnionContextList[i].PivotVariable.GetEdgeType();
                if (isSameType == false) throw new NotImplementedException();
            }
            return UnionContextList.First().PivotVariable.GetEdgeType();
        }

        internal override void InV(GremlinToSqlContext currentContext)
        {
            currentContext.InV(this);
        }

        internal override void OutV(GremlinToSqlContext currentContext)
        {
            currentContext.OutV(this);
        }

        internal override void OtherV(GremlinToSqlContext currentContext)
        {
            currentContext.OtherV(this);
        }
    }

    internal class GremlinUnionScalarVariable : GremlinUnionVariable
    {
        public GremlinUnionScalarVariable(List<GremlinToSqlContext> unionContextList)
            : base(unionContextList, GremlinVariableType.Scalar)
        {
        }

    }

    internal class GremlinUnionNullVariable : GremlinUnionVariable
    {
        public GremlinUnionNullVariable(List<GremlinToSqlContext> unionContextList)
            : base(unionContextList, GremlinVariableType.NULL)
        {
        }
    }
}
