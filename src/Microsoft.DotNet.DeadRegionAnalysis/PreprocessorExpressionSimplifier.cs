using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp;

namespace Microsoft.DotNet.DeadRegionAnalysis
{
    /// <summary>
    /// Regions with expressions which evaluate to "varying" will not be removed by the analysis engine.
    /// Such expressions can be simplified by collapsing binary expressions of the forms:
    /// 
    /// true && varying
    /// false || varying
    /// 
    /// to "varying".
    /// 
    /// When removing all references to a given preprocessor symbol which has a known value, the symbol
    /// in question may also happen to appear in expressions which evaluate to varying. By simplifying
    /// such expressions, we alleviate the need to do any manual clean up of references to the symbol
    /// after removing dead conditional regions.
    /// </summary>
    internal class PreprocessorExpressionSimplifier : CSharpSyntaxVisitor<CSharpSyntaxNode>
    {
        CompoundPreprocessorExpressionEvaluator m_expressionEvaluator;
    }
}
