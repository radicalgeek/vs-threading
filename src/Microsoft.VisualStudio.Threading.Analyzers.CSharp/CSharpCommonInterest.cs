﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Text;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.Diagnostics;

    internal static class CSharpCommonInterest
    {
        internal static readonly IImmutableSet<SyntaxKind> MethodSyntaxKinds = ImmutableHashSet.Create(
            SyntaxKind.ConstructorDeclaration,
            SyntaxKind.MethodDeclaration,
            SyntaxKind.OperatorDeclaration,
            SyntaxKind.AnonymousMethodExpression,
            SyntaxKind.SimpleLambdaExpression,
            SyntaxKind.ParenthesizedLambdaExpression,
            SyntaxKind.GetAccessorDeclaration,
            SyntaxKind.SetAccessorDeclaration,
            SyntaxKind.AddAccessorDeclaration,
            SyntaxKind.RemoveAccessorDeclaration);

        /// <summary>
        /// This is an explicit rule to ignore the code that was generated by Xaml2CS.
        /// </summary>
        /// <remarks>
        /// The generated code has the comments like this:
        /// <![CDATA[
        ///   //------------------------------------------------------------------------------
        ///   // <auto-generated>
        /// ]]>
        /// This rule is based on the fact the keyword "&lt;auto-generated&gt;" should be found in the comments.
        /// </remarks>
        internal static bool ShouldIgnoreContext(SyntaxNodeAnalysisContext context)
        {
            NamespaceDeclarationSyntax? namespaceDeclaration = context.Node.FirstAncestorOrSelf<NamespaceDeclarationSyntax>();
            if (namespaceDeclaration is object)
            {
                foreach (SyntaxTrivia trivia in namespaceDeclaration.NamespaceKeyword.GetAllTrivia())
                {
                    const string autoGeneratedKeyword = @"<auto-generated>";
                    if (trivia.FullSpan.Length > autoGeneratedKeyword.Length
                        && trivia.ToString().Contains(autoGeneratedKeyword))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        internal static void InspectMemberAccess(
            SyntaxNodeAnalysisContext context,
            MemberAccessExpressionSyntax? memberAccessSyntax,
            DiagnosticDescriptor descriptor,
            IEnumerable<CommonInterest.SyncBlockingMethod> problematicMethods,
            bool ignoreIfInsideAnonymousDelegate = false)
        {
            if (descriptor is null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            if (memberAccessSyntax is null)
            {
                return;
            }

            if (ShouldIgnoreContext(context))
            {
                return;
            }

            if (ignoreIfInsideAnonymousDelegate && context.Node.FirstAncestorOrSelf<AnonymousFunctionExpressionSyntax>() is object)
            {
                // We do not analyze JTF.Run inside anonymous functions because
                // they are so often used as callbacks where the signature is constrained.
                return;
            }

            if (CSharpUtils.IsWithinNameOf(context.Node as ExpressionSyntax))
            {
                // We do not consider arguments to nameof( ) because they do not represent invocations of code.
                return;
            }

            ITypeSymbol? typeReceiver = context.SemanticModel.GetTypeInfo(memberAccessSyntax.Expression).Type;
            if (typeReceiver is object)
            {
                foreach (CommonInterest.SyncBlockingMethod item in problematicMethods)
                {
                    if (memberAccessSyntax.Name.Identifier.Text == item.Method.Name &&
                        typeReceiver.Name == item.Method.ContainingType.Name &&
                        typeReceiver.BelongsToNamespace(item.Method.ContainingType.Namespace))
                    {
                        Location? location = memberAccessSyntax.Name.GetLocation();
                        context.ReportDiagnostic(Diagnostic.Create(descriptor, location));
                    }
                }
            }
        }
    }
}
