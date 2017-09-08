﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.Logging.Internal;

namespace Microsoft.Extensions.Logging.Analyzers
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class LogFormatAnalyzer : DiagnosticAnalyzer
    {
        public LogFormatAnalyzer()
        {
            SupportedDiagnostics = ImmutableArray.Create(new[]
            {
                Descriptors.MEL1NumericsInFormatString,
                Descriptors.MEL2ConcatenationInFormatString,
                Descriptors.MEL3FormatParameterCountMismatch
            });
        }

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; }

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();

            context.RegisterCompilationStartAction(analysisContext =>
            {
                var loggerExtensionsType = analysisContext.Compilation.GetTypeByMetadataName(typeof(LoggerExtensions).FullName);
                if (loggerExtensionsType == null)
                    return;

                analysisContext.RegisterSyntaxNodeAction(syntaxContext => AnalyzeInvocation(syntaxContext, loggerExtensionsType), SyntaxKind.InvocationExpression);
            });
        }

        private void AnalyzeInvocation(SyntaxNodeAnalysisContext syntaxContext, INamedTypeSymbol loggerExtensionsType)
        {
            var invocation = (InvocationExpressionSyntax)syntaxContext.Node;

            var symbolInfo = ModelExtensions.GetSymbolInfo(syntaxContext.SemanticModel, invocation, syntaxContext.CancellationToken);
            if (symbolInfo.Symbol?.Kind != SymbolKind.Method)
                return;

            var methodSymbol = (IMethodSymbol)symbolInfo.Symbol;

            if (methodSymbol.MethodKind != MethodKind.ReducedExtension || methodSymbol.ContainingType != loggerExtensionsType)
                return;

            if (FindLogParameters(methodSymbol, out var messageArgument, out var paramsArgument))
            {
                int paramsCount = 0;
                ExpressionSyntax formatExpression = null;
                bool argsIsArray = false;

                foreach (var argument in invocation.ArgumentList.Arguments)
                {
                    var parameter = DetermineParameter(argument, syntaxContext.SemanticModel, syntaxContext.CancellationToken);
                    if (Equals(parameter, messageArgument))
                    {
                        formatExpression = argument.Expression;
                    }
                    else if (Equals(parameter, paramsArgument))
                    {
                        var parameterType = syntaxContext.SemanticModel.GetTypeInfo(argument.Expression).ConvertedType;
                        if (parameterType == null)
                        {
                            return;
                        }

                        //Detect if current argument can be passed directly to args
                        argsIsArray = parameterType.TypeKind == TypeKind.Array && ((IArrayTypeSymbol)parameterType).ElementType.SpecialType == SpecialType.System_Object;

                        paramsCount++;
                    }
                }

                AnalyzeFormatArgument(syntaxContext, formatExpression, paramsCount, argsIsArray);
            }
        }

        private void AnalyzeFormatArgument(SyntaxNodeAnalysisContext syntaxContext, ExpressionSyntax formatExpression, int paramsCount, bool argsIsArray)
        {
            var text = TryGetFormatText(formatExpression);
            if (text == null)
            {
                syntaxContext.ReportDiagnostic(Diagnostic.Create(Descriptors.MEL2ConcatenationInFormatString, formatExpression.GetLocation()));
                return;
            }

            LogValuesFormatter formatter;
            try
            {
                formatter = new LogValuesFormatter(text);
            }
            catch (Exception)
            {
                return;
            }

            foreach (var valueName in formatter.ValueNames)
            {
                if (int.TryParse(valueName, out _))
                {
                    syntaxContext.ReportDiagnostic(Diagnostic.Create(Descriptors.MEL1NumericsInFormatString, formatExpression.GetLocation()));
                    break;
                }
            }

            var argsPassedDirectly = argsIsArray && paramsCount == 1;
            if (!argsPassedDirectly && paramsCount != formatter.ValueNames.Count)
            {
                syntaxContext.ReportDiagnostic(Diagnostic.Create(Descriptors.MEL3FormatParameterCountMismatch, formatExpression.GetLocation()));
            }
        }

        private string TryGetFormatText(ExpressionSyntax argumentExpression)
        {
            switch (argumentExpression)
            {
                case LiteralExpressionSyntax literal when literal.Token.IsKind(SyntaxKind.StringLiteralToken):
                    return literal.Token.ValueText;
                case InterpolatedStringExpressionSyntax interpolated:
                    var text = "";
                    foreach (var interpolatedStringContentSyntax in interpolated.Contents)
                    {
                        if (interpolatedStringContentSyntax is InterpolatedStringTextSyntax textSyntax)
                        {
                            text += textSyntax.TextToken.ValueText;
                        }
                        else
                        {
                            return null;
                        }
                    }
                    return text;
                case InvocationExpressionSyntax invocation when IsNameOfInvocation(invocation):
                    // return placeholder from here because actual value is not required for analysis and is hard to get
                    return "NAMEOF";
                case ParenthesizedExpressionSyntax parenthesized:
                    return TryGetFormatText(parenthesized.Expression);
                case BinaryExpressionSyntax binary when binary.OperatorToken.IsKind(SyntaxKind.PlusToken):
                    var leftText = TryGetFormatText(binary.Left);
                    var rightText = TryGetFormatText(binary.Right);

                    if (leftText != null && rightText != null)
                    {
                        return leftText + rightText;
                    }

                    return null;
                default:
                    return null;
            }
        }

        private bool FindLogParameters(IMethodSymbol methodSymbol, out IParameterSymbol message, out IParameterSymbol arguments)
        {
            message = null;
            arguments = null;
            for (int i = 0; i < methodSymbol.Parameters.Length; i++)
            {
                var parameter = methodSymbol.Parameters[i];

                if (parameter.Type.SpecialType == SpecialType.System_String &&
                    parameter.Name == "message" ||
                    parameter.Name == "messageFormat")
                {
                    message = parameter;
                }

                if (parameter.IsParams &&
                    parameter.Name == "args")
                {
                    arguments = parameter;
                }
            }
            return message != null && arguments != null;
        }

        private static bool IsNameOfInvocation(InvocationExpressionSyntax invocation)
        {
            return invocation.Expression is IdentifierNameSyntax identifierName &&
                   (identifierName.Identifier.IsKind(SyntaxKind.NameOfKeyword) ||
                   identifierName.Identifier.ToString() == SyntaxFacts.GetText(SyntaxKind.NameOfKeyword));
        }

        private static IParameterSymbol DetermineParameter(
            ArgumentSyntax argument,
            SemanticModel semanticModel,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var argumentList = argument.Parent as BaseArgumentListSyntax;
            if (argumentList == null)
            {
                return null;
            }

            var invocableExpression = argumentList.Parent as ExpressionSyntax;
            if (invocableExpression == null)
            {
                return null;
            }

            var symbol = semanticModel.GetSymbolInfo(invocableExpression, cancellationToken).Symbol as IMethodSymbol;
            if (symbol == null)
            {
                return null;
            }

            var parameters = symbol.Parameters;

            // Handle named argument
            if (argument.NameColon != null && !argument.NameColon.IsMissing)
            {
                var name = argument.NameColon.Name.Identifier.ValueText;
                return parameters.FirstOrDefault(p => p.Name == name);
            }

            // Handle positional argument
            var index = argumentList.Arguments.IndexOf(argument);
            if (index < 0)
            {
                return null;
            }

            if (index < parameters.Length)
            {
                return parameters[index];
            }

            var lastParameter = parameters.LastOrDefault();
            if (lastParameter == null)
            {
                return null;
            }

            if (lastParameter.IsParams)
            {
                return lastParameter;
            }

            return null;
        }
    }
}