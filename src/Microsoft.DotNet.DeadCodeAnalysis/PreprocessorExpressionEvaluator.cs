using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.DotNet.DeadCodeAnalysis
{
    internal class PreprocessorExpressionEvaluator : CSharpSyntaxVisitor<SymbolState>
    {
        private Dictionary<string, SymbolState> m_symbolStates;

        public PreprocessorExpressionEvaluator(Dictionary<string, SymbolState> symbolStates)
        {
            Debug.Assert(symbolStates != null);
            m_symbolStates = symbolStates;
        }

        public override SymbolState VisitLiteralExpression(LiteralExpressionSyntax node)
        {
            switch (node.CSharpKind())
            {
                case SyntaxKind.TrueLiteralExpression:
                    return SymbolState.AlwaysEnabled;
                case SyntaxKind.FalseLiteralExpression:
                    return SymbolState.AlwaysDisabled;
                default:
                    Debug.Assert(false, "Preprocessor expressions can only have true & false literals");
                    return SymbolState.Varying;
            }
        }

        public override SymbolState VisitIdentifierName(IdentifierNameSyntax node)
        {
            SymbolState state;
            if (m_symbolStates.TryGetValue(node.ToString(), out state))
            {
                return state;
            }
            else
            {
                return SymbolState.AlwaysDisabled;
            }
        }

        public override SymbolState VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
        {
            Debug.Assert(node.CSharpKind() == SyntaxKind.LogicalNotExpression);

            SymbolState innerState = node.Operand.Accept(this);
            if (innerState == SymbolState.Varying)
            {
                return SymbolState.Varying;
            }
            else
            {
                // Negate the inner state
                return (SymbolState)((int)innerState ^ 1);
            }
        }

        public override SymbolState VisitBinaryExpression(BinaryExpressionSyntax node)
        {
            SymbolState left = node.Left.Accept(this);
            SymbolState right = node.Right.Accept(this);

            switch (node.CSharpKind())
            {
                case SyntaxKind.LogicalAndExpression:
                    if (left == SymbolState.AlwaysDisabled || right == SymbolState.AlwaysDisabled)
                    {
                        // false && anything == false
                        return SymbolState.AlwaysDisabled;
                    }
                    if (left == SymbolState.AlwaysEnabled && right == SymbolState.AlwaysEnabled)
                    {
                        // true && true == true
                        return SymbolState.AlwaysEnabled;
                    }
                    // true && varying == varying
                    return SymbolState.Varying;

                case SyntaxKind.LogicalOrExpression:
                    if (left == SymbolState.AlwaysEnabled || right == SymbolState.AlwaysEnabled)
                    {
                        // true || anything == true
                        return SymbolState.AlwaysEnabled;
                    }
                    if (left == SymbolState.AlwaysDisabled && right == SymbolState.AlwaysDisabled)
                    {
                        // false || false == false
                        return SymbolState.AlwaysDisabled;
                    }
                    // false || varying == varying
                    return SymbolState.Varying;
                default:
                    Debug.Assert(false, "Preprocessor expressions can only have logical or/and binary expressions");
                    return SymbolState.Varying;
            }
        }

        public override SymbolState VisitParenthesizedExpression(ParenthesizedExpressionSyntax node)
        {
            return node.Expression.Accept(this);
        }
    }
}
