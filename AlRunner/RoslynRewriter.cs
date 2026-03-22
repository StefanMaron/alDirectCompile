using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// No namespace - must be accessible from Program.cs top-level statements

/// <summary>
/// Roslyn CSharpSyntaxRewriter that transforms BC-generated C# into standalone code.
/// Replaces the fragile regex-based CSharpRewriter (now RegexRewriter) with a proper
/// syntax-tree-based approach.
/// </summary>
public class RoslynRewriter : CSharpSyntaxRewriter
{
    private static readonly HashSet<string> BcAttributeNames = new(StringComparer.Ordinal)
    {
        "NavCodeunitOptions",
        "NavFunctionVisibility",
        "NavCaption",
        "NavName",
        "NavTest",
        "SignatureSpan",
        "SourceSpans",
        "ReturnValue",
        "NavObjectId",
        "NavByReferenceAttribute",
    };

    private static readonly HashSet<string> RemoveUsings = new(StringComparer.Ordinal)
    {
        "Microsoft.Dynamics.Nav.Runtime.Extensions",
        "Microsoft.Dynamics.Nav.Runtime.Report",
        "Microsoft.Dynamics.Nav.EventSubscription",
        "Microsoft.Dynamics.Nav.Common.Language",
    };

    /// <summary>
    /// Methods that take ITreeObject as first arg which we strip (e.g., value.ALByValue(this))
    /// </summary>
    // Methods on BC types that accept ITreeObject/NavRecord but should be no-ops in standalone mode
    private static readonly HashSet<string> StripEntireCallMethods = new(StringComparer.Ordinal)
    {
        "ALGetTable", // NavRecordRef.ALGetTable(NavRecord) — record assertion methods only
        "ALClose",    // NavRecordRef.ALClose()
        "RunEvent",   // NavEventScope.RunEvent() — event subscriber dispatch, no-op standalone
    };

    private static readonly HashSet<string> StripITreeObjectArgMethods = new(StringComparer.Ordinal)
    {
        "ALByValue", "ModifyLength",
    };

    /// <summary>
    /// Names of .Target methods on record handles that should have .Target stripped.
    /// </summary>
    private static readonly HashSet<string> RecordTargetMethods = new(StringComparer.Ordinal)
    {
        "ALInit", "ALInsert", "ALModify", "ALGet", "ALFind", "ALNext", "ALDelete",
        "ALDeleteAll", "ALCount", "ALSetRange", "ALSetFilter", "ALFindSet",
        "ALFindFirst", "ALFindLast", "ALIsEmpty", "ALCalcFields", "ALSetCurrentKey",
        "ALReset", "ALCopy", "ALTestField", "ALValidate", "ALRename",
        "ALLockTable", "ALCalcSums",
        "SetFieldValueSafe", "GetFieldValueSafe", "GetFieldRefSafe",
    };

    public static string Rewrite(string csharp)
    {
        var tree = CSharpSyntaxTree.ParseText(csharp);
        var root = tree.GetRoot();

        var rewriter = new RoslynRewriter();

        var newRoot = rewriter.Visit(root);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    // -----------------------------------------------------------------------
    // Using directives
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitUsingDirective(UsingDirectiveSyntax node)
    {
        var name = node.NamespaceOrType.ToString();
        if (RemoveUsings.Contains(name))
            return null;
        return base.VisitUsingDirective(node);
    }

    // -----------------------------------------------------------------------
    // Namespace: inject "using AlRunner.Runtime;" after opening brace
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
    {
        // First recurse into children
        var visited = (NamespaceDeclarationSyntax)base.VisitNamespaceDeclaration(node)!;

        // Add using AlRunner.Runtime if not already present
        bool hasRuntimeUsing = visited.Usings.Any(u =>
            u.NamespaceOrType.ToString() == "AlRunner.Runtime");

        if (!hasRuntimeUsing)
        {
            var usingDirective = SyntaxFactory.UsingDirective(
                SyntaxFactory.ParseName("AlRunner.Runtime"));
            visited = visited.AddUsings(usingDirective);
        }

        return visited;
    }

    // Also handle file-scoped namespaces
    public override SyntaxNode? VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
    {
        var visited = (FileScopedNamespaceDeclarationSyntax)base.VisitFileScopedNamespaceDeclaration(node)!;

        bool hasRuntimeUsing = visited.Usings.Any(u =>
            u.NamespaceOrType.ToString() == "AlRunner.Runtime");

        if (!hasRuntimeUsing)
        {
            var usingDirective = SyntaxFactory.UsingDirective(
                SyntaxFactory.ParseName("AlRunner.Runtime"));
            visited = visited.AddUsings(usingDirective);
        }

        return visited;
    }

    // -----------------------------------------------------------------------
    // Attribute lists: remove BC-specific attributes
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitAttributeList(AttributeListSyntax node)
    {
        var kept = new SeparatedSyntaxList<AttributeSyntax>();
        foreach (var attr in node.Attributes)
        {
            var attrName = GetSimpleAttributeName(attr);
            if (!BcAttributeNames.Contains(attrName))
                kept = kept.Add(attr);
        }

        if (kept.Count == 0)
            return null; // remove entire attribute list

        if (kept.Count == node.Attributes.Count)
            return base.VisitAttributeList(node); // nothing changed

        return node.WithAttributes(kept);
    }

    private static string GetSimpleAttributeName(AttributeSyntax attr)
    {
        // Extract the last identifier from potentially qualified name
        var name = attr.Name;
        if (name is QualifiedNameSyntax qns)
            return qns.Right.Identifier.Text;
        if (name is IdentifierNameSyntax ins)
            return ins.Identifier.Text;
        return name.ToString();
    }

    // -----------------------------------------------------------------------
    // Class declarations: handle base classes, remove BC members, add _parent field
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        // Detect if this is a scope class BEFORE visiting children.
        // We need to know the enclosing class name for _parent field type.
        bool isScopeClass = false;
        string? enclosingClassName = null;
        if (node.BaseList != null)
        {
            foreach (var baseType in node.BaseList.Types)
            {
                var typeText = baseType.Type.ToString();
                if (typeText.StartsWith("NavMethodScope<") || typeText.StartsWith("NavTriggerMethodScope<"))
                {
                    isScopeClass = true;
                    // Extract the generic type parameter as the enclosing class name
                    // NavMethodScope<Codeunit139771> -> Codeunit139771
                    var ltIdx = typeText.IndexOf('<');
                    var gtIdx = typeText.IndexOf('>');
                    if (ltIdx >= 0 && gtIdx > ltIdx)
                        enclosingClassName = typeText.Substring(ltIdx + 1, gtIdx - ltIdx - 1);
                    break;
                }
            }
        }

        // First, visit children recursively
        var visited = (ClassDeclarationSyntax)base.VisitClassDeclaration(node)!;

        // Handle base class list
        if (visited.BaseList != null)
        {
            var newTypes = new SeparatedSyntaxList<BaseTypeSyntax>();
            foreach (var baseType in visited.BaseList.Types)
            {
                var typeText = baseType.Type.ToString();

                if (typeText == "NavCodeunit" || typeText == "NavTestCodeunit" || typeText == "NavRecord"
                    || typeText == "NavFormExtension" || typeText == "NavRecordExtension"
                    || typeText == "NavEventScope" || typeText == "NavUpgradeCodeunit")
                {
                    // Remove these base classes entirely
                    continue;
                }

                if (typeText.StartsWith("NavMethodScope<") || typeText.StartsWith("NavTriggerMethodScope<")
                    || typeText.StartsWith("NavEventMethodScope<"))
                {
                    // Replace with AlScope
                    var alScopeType = SyntaxFactory.SimpleBaseType(
                        SyntaxFactory.ParseTypeName("AlScope"));
                    newTypes = newTypes.Add(alScopeType);
                    continue;
                }

                newTypes = newTypes.Add(baseType);
            }

            if (newTypes.Count == 0)
                visited = visited.WithBaseList(null);
            else
                visited = visited.WithBaseList(SyntaxFactory.BaseList(newTypes));
        }

        // Remove specific members
        var membersToKeep = new SyntaxList<MemberDeclarationSyntax>();
        foreach (var member in visited.Members)
        {
            if (ShouldRemoveMember(member, visited))
                continue;
            membersToKeep = membersToKeep.Add(member);
        }

        visited = visited.WithMembers(membersToKeep);

        // For scope classes: add a _parent field of the enclosing class type
        if (isScopeClass && enclosingClassName != null)
        {
            var parentField = SyntaxFactory.FieldDeclaration(
                SyntaxFactory.VariableDeclaration(
                    SyntaxFactory.ParseTypeName(enclosingClassName))
                .WithVariables(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator("_parent"))))
                .WithModifiers(SyntaxFactory.TokenList(
                    SyntaxFactory.Token(SyntaxKind.PrivateKeyword)));

            // Insert the _parent field at the beginning
            visited = visited.WithMembers(visited.Members.Insert(0, parentField));
        }

        return visited;
    }

    private static bool ShouldRemoveMember(MemberDeclarationSyntax member, ClassDeclarationSyntax parentClass)
    {
        // Remove BC constructor: public CodeunitXXX(ITreeObject parent) : base(parent, NNN) { }
        // Remove Record constructor: public RecordXXX(ITreeObject parent, NCLMetaTable ...) : base(...) { }
        if (member is ConstructorDeclarationSyntax ctor)
        {
            var paramText = ctor.ParameterList.ToString();
            if (paramText.Contains("ITreeObject parent"))
                return true;
        }

        // Remove methods
        if (member is MethodDeclarationSyntax method)
        {
            var name = method.Identifier.Text;

            // Remove __Construct
            if (name == "__Construct")
                return true;

            // Remove OnInvoke
            if (name == "OnInvoke" && method.ParameterList.Parameters.Count == 2)
            {
                var firstParam = method.ParameterList.Parameters[0].Type?.ToString();
                var secondParam = method.ParameterList.Parameters[1].Type?.ToString();
                if (firstParam == "int" && secondParam == "object[]")
                    return true;
            }

            // Remove OnRun with parameters (the codeunit's OnRun, not the scope's)
            if (name == "OnRun" && method.ParameterList.Parameters.Count > 0)
                return true;

            // Remove GetMethodScopeFlags (BC runtime permission checking, irrelevant for standalone)
            if (name == "GetMethodScopeFlags")
                return true;

            // Remove BC interface dispatch methods (used by NavInterfaceHandle runtime)
            if (name == "IsInterfaceOfType" || name == "IsInterfaceMethod")
                return true;

            // Remove Page/Extension-specific methods that reference BC Page runtime
            if (name == "OnMetadataLoaded" || name == "EvaluateCaptionClass"
                || name == "OnEvaluateCaptionClass" || name == "RegisterDynamicCaptionExpression"
                || name == "EnsureGlobalVariablesInitialized" || name == "CallEvaluateCaptionClassExtensionMethod"
                || name == "CallOnMetadataLoadedExtensionMethod")
                return true;
        }

        // Remove specific properties
        if (member is PropertyDeclarationSyntax prop)
        {
            var name = prop.Identifier.Text;

            // public override string ObjectName => "...";
            if (name == "ObjectName")
                return true;

            // public override bool IsCompiledForOnPremise => true;
            if (name == "IsCompiledForOnPremise")
                return true;

            // public override bool IsSingleInstance => false;
            if (name == "IsSingleInstance")
                return true;

            // private RecordXXX Rec => ...; or private NavRecord Rec => (NavRecord)base.ParentObject;
            if (name == "Rec" || name == "xRec")
                return true;

            // protected override uint RawScopeId { get => ...; set => ...; }
            if (name == "RawScopeId")
                return true;

            // private new NavRecord ParentObject => ...;
            // private new NavForm CurrPage => ...;
            if (name == "ParentObject" || name == "CurrPage")
                return true;

            // protected override uint[] IndirectPermissionList => ...;
            if (name == "IndirectPermissionList")
                return true;

            // public override NavEventScope EventScope { get; set; }
            if (name == "EventScope")
                return true;

            // public override int MethodId => N;
            if (name == "MethodId")
                return true;
        }

        // Remove static αscopeId field (Unicode \u03b1 prefix)
        if (member is FieldDeclarationSyntax field)
        {
            foreach (var variable in field.Declaration.Variables)
            {
                var name = variable.Identifier.ValueText;
                var text = variable.Identifier.Text;
                // Match by ValueText, Text, or fallback pattern: any field ending with "scopeId"
                if (name == "\u03b1scopeId" || text == "\u03b1scopeId" ||
                    name.EndsWith("scopeId") || text.EndsWith("scopeId"))
                    return true;
            }
        }

        return false;
    }

    // -----------------------------------------------------------------------
    // Method declarations: remove 'override' from OnClear
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;

        // Remove 'override' keyword from methods whose base class was removed.
        // We strip NavCodeunit/NavRecord/NavFormExtension/NavRecordExtension/NavEventScope,
        // so any 'override' in those classes becomes invalid.
        if (visited.Modifiers.Any(m => m.IsKind(SyntaxKind.OverrideKeyword)))
        {
            var parentClass = node.Parent as ClassDeclarationSyntax;
            if (parentClass?.BaseList != null)
            {
                bool hadRemovedBase = parentClass.BaseList.Types.Any(t =>
                {
                    var txt = t.Type.ToString();
                    return txt == "NavCodeunit" || txt == "NavTestCodeunit" || txt == "NavRecord"
                        || txt == "NavFormExtension" || txt == "NavRecordExtension"
                        || txt == "NavEventScope" || txt == "NavUpgradeCodeunit";
                });
                if (hadRemovedBase)
                {
                    var newModifiers = SyntaxFactory.TokenList(
                        visited.Modifiers.Where(m => !m.IsKind(SyntaxKind.OverrideKeyword)));
                    visited = visited.WithModifiers(newModifiers);
                }
            }
        }

        return visited;
    }

    // -----------------------------------------------------------------------
    // Constructor declarations: handle scope ctors with _parent field
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
    {
        var visited = (ConstructorDeclarationSyntax)base.VisitConstructorDeclaration(node)!;

        // For scope constructors (internal constructors in nested classes that inherit from AlScope),
        // the base class has been replaced with AlScope which has a parameterless constructor.
        // Remove ALL base(...) initializers from these constructors.
        if (visited.Initializer != null && visited.Initializer.Kind() == SyntaxKind.BaseConstructorInitializer)
        {
            // Check if this is a scope constructor by looking for parent or βparent references,
            // or simply any base() call on an internal constructor (scope constructors are internal).
            var isInternal = visited.Modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword));
            var hasParentArg = visited.Initializer.ArgumentList.Arguments
                .Any(a => a.Expression is IdentifierNameSyntax id &&
                    (id.Identifier.ValueText.Contains("parent") || id.Identifier.Text.Contains("parent")));

            if (hasParentArg || isInternal)
            {
                visited = visited.WithInitializer(null);
            }
        }

        // Handle βparent parameter: KEEP it but add _parent assignment to constructor body.
        // Instead of removing the parameter, we keep it and add: this._parent = βparent;
        if (visited.ParameterList.Parameters.Count > 0)
        {
            var firstParam = visited.ParameterList.Parameters[0];
            var paramName = firstParam.Identifier.ValueText;
            if (paramName.Contains("parent") || firstParam.Identifier.Text.Contains("parent"))
            {
                var typeText = firstParam.Type?.ToString() ?? "";
                if (typeText.StartsWith("Codeunit"))
                {
                    // Keep the parameter, but add _parent assignment at the start of the body
                    var paramIdentifier = firstParam.Identifier.Text;
                    var assignmentStatement = SyntaxFactory.ParseStatement(
                        $"this._parent = {paramIdentifier};");

                    if (visited.Body != null)
                    {
                        var newStatements = visited.Body.Statements.Insert(0, assignmentStatement);
                        visited = visited.WithBody(visited.Body.WithStatements(newStatements));
                    }
                    else if (visited.ExpressionBody != null)
                    {
                        // Convert expression body to block body with assignment + expression
                        var exprStatement = SyntaxFactory.ExpressionStatement(visited.ExpressionBody.Expression);
                        var body = SyntaxFactory.Block(assignmentStatement, exprStatement);
                        visited = visited.WithExpressionBody(null)
                            .WithSemicolonToken(SyntaxFactory.MissingToken(SyntaxKind.SemicolonToken))
                            .WithBody(body);
                    }

                    // Do NOT remove the parameter - keep it so callers can pass 'this'
                }
            }
        }

        return visited;
    }

    // -----------------------------------------------------------------------
    // Identifier names: type replacements
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        var text = node.Identifier.Text;

        // INavRecordHandle -> MockRecordHandle
        if (text == "INavRecordHandle")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockRecordHandle"));

        // NavRecordHandle -> MockRecordHandle (used in new NavRecordHandle(...))
        if (text == "NavRecordHandle")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockRecordHandle"));

        // NavCodeunitHandle -> MockCodeunitHandle
        if (text == "NavCodeunitHandle")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockCodeunitHandle"));

        // NavInterfaceHandle -> MockInterfaceHandle
        if (text == "NavInterfaceHandle")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockInterfaceHandle"));

        // NavVariant -> object (Variant in AL is just object in standalone)
        if (text == "NavVariant")
            return node.WithIdentifier(SyntaxFactory.Identifier("object"));

        // NavTextConstant -> NavText (avoid BC runtime initialization)
        if (text == "NavTextConstant")
            return node.WithIdentifier(SyntaxFactory.Identifier("NavText"));

        // NavEventScope -> object (event scope type used for static fields)
        if (text == "NavEventScope")
            return node.WithIdentifier(SyntaxFactory.Identifier("object"));

        return base.VisitIdentifierName(node);
    }

    // -----------------------------------------------------------------------
    // Object creation expressions
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        var visited = (ObjectCreationExpressionSyntax)base.VisitObjectCreationExpression(node)!;
        var typeText = visited.Type.ToString();

        // new NavRecordHandle(this, NNN, false, SecurityFiltering.XXX) -> new MockRecordHandle(NNN)
        // After identifier replacement, this is already MockRecordHandle
        if (typeText == "MockRecordHandle" && visited.ArgumentList != null &&
            visited.ArgumentList.Arguments.Count >= 4)
        {
            // The second argument is the table ID
            var tableIdArg = visited.ArgumentList.Arguments[1];
            var newArgs = SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(tableIdArg.Expression)));
            return visited.WithArgumentList(newArgs);
        }

        // new MockCodeunitHandle(this, NNN) -> MockCodeunitHandle.Create(NNN)
        if (typeText == "MockCodeunitHandle" && visited.ArgumentList != null &&
            visited.ArgumentList.Arguments.Count == 2)
        {
            var firstArgText = visited.ArgumentList.Arguments[0].Expression.ToString();
            if (firstArgText == "this")
            {
                var codeunitId = visited.ArgumentList.Arguments[1].Expression;
                return SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("MockCodeunitHandle"),
                        SyntaxFactory.IdentifierName("Create")),
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(codeunitId))));
            }
        }

        // new MockInterfaceHandle(this) -> new MockInterfaceHandle()
        // Strip the 'this' arg since MockInterfaceHandle doesn't need ITreeObject
        if (typeText == "MockInterfaceHandle" && visited.ArgumentList != null &&
            visited.ArgumentList.Arguments.Count == 1)
        {
            var firstArgText = visited.ArgumentList.Arguments[0].Expression.ToString();
            if (firstArgText == "this")
            {
                return visited.WithArgumentList(SyntaxFactory.ArgumentList());
            }
        }

        // new NavRecordRef(this, SecurityFiltering.XXX) -> new NavRecordRef(null!, SecurityFiltering.XXX)
        // Replace scope 'this' (ITreeObject) with null since we don't have ITreeObject
        if (typeText == "NavRecordRef" && visited.ArgumentList != null &&
            visited.ArgumentList.Arguments.Count >= 1)
        {
            var firstArgText = visited.ArgumentList.Arguments[0].Expression.ToString();
            if (firstArgText == "this")
            {
                var newArgs = visited.ArgumentList.Arguments.Replace(
                    visited.ArgumentList.Arguments[0],
                    SyntaxFactory.Argument(
                        SyntaxFactory.PostfixUnaryExpression(
                            SyntaxKind.SuppressNullableWarningExpression,
                            SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression))));
                return visited.WithArgumentList(SyntaxFactory.ArgumentList(newArgs));
            }
        }

        // new NavTextConstant(langIds, strings, null, null) -> new NavText(strings[0])
        // After VisitIdentifierName, NavTextConstant is already renamed to NavText
        // NavTextConstant triggers NavEnvironment initialization; replace with simple NavText
        if ((typeText == "NavTextConstant" || typeText == "NavText") && visited.ArgumentList != null &&
            visited.ArgumentList.Arguments.Count >= 4)
        {
            // The second argument is the string array: new string[] { "the text" }
            var stringsArg = visited.ArgumentList.Arguments[1].Expression;
            if (stringsArg is ImplicitArrayCreationExpressionSyntax implArr && implArr.Initializer.Expressions.Count > 0)
            {
                return SyntaxFactory.ObjectCreationExpression(
                    SyntaxFactory.ParseTypeName("NavText"))
                    .WithArgumentList(SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(implArr.Initializer.Expressions[0]))));
            }
            if (stringsArg is ArrayCreationExpressionSyntax arrCreate && arrCreate.Initializer != null && arrCreate.Initializer.Expressions.Count > 0)
            {
                return SyntaxFactory.ObjectCreationExpression(
                    SyntaxFactory.ParseTypeName("NavText"))
                    .WithArgumentList(SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(arrCreate.Initializer.Expressions[0]))));
            }
        }

        // NOTE: We no longer strip 'this' from scope constructor calls.
        // Scope constructors now keep the βparent parameter and store it as _parent.

        return visited;
    }

    // -----------------------------------------------------------------------
    // Invocation expressions: NavDialog, StmtHit, CStmtHit, NavRuntimeHelpers, ALCompiler
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        // Check for StmtHit(N) and CStmtHit(N) BEFORE recursing into children
        if (node.Expression is IdentifierNameSyntax stmtIdent)
        {
            if (stmtIdent.Identifier.Text == "StmtHit")
            {
                // Will be removed at statement level (VisitExpressionStatement)
                // But if encountered as expression, return as-is for now
                return base.VisitInvocationExpression(node);
            }

            if (stmtIdent.Identifier.Text == "CStmtHit")
            {
                // Replace CStmtHit(N) with true
                return SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression);
            }
        }

        // Now recurse into children first
        var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;

        // NavDialog.ALMessage(this.Session, System.Guid.Parse("..."), fmt, args...)
        // -> AlDialog.Message(fmt, args...)
        if (visited.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            var exprText = memberAccess.Expression.ToString();
            var methodName = memberAccess.Name.Identifier.Text;

            // NavDialog.ALMessage / NavDialog.ALError
            if (exprText == "NavDialog" && (methodName == "ALMessage" || methodName == "ALError"))
            {
                var newMethodName = methodName == "ALMessage" ? "Message" : "Error";
                var args = visited.ArgumentList.Arguments;

                // Skip first two args (this.Session and System.Guid.Parse("..."))
                if (args.Count >= 2)
                {
                    var keptArgs = new SeparatedSyntaxList<ArgumentSyntax>();
                    for (int i = 2; i < args.Count; i++)
                        keptArgs = keptArgs.Add(args[i]);

                    return SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("AlDialog"),
                            SyntaxFactory.IdentifierName(newMethodName)),
                        SyntaxFactory.ArgumentList(keptArgs));
                }
            }

            // NavRuntimeHelpers.CompilationError(...)
            if (exprText == "NavRuntimeHelpers" && methodName == "CompilationError")
            {
                // Replace with: throw new InvalidOperationException("Compilation error")
                // But this is an expression, we need to return an invocation.
                // The throw will be handled in VisitExpressionStatement.
                // Mark it for statement-level replacement by keeping it recognizable.
                return visited;
            }

            // NCLEnumMetadata.Create(N) -> NCLOptionMetadata.Default
            // NCLEnumMetadata.Create goes through NavGlobal.MetadataProvider -> NavEnvironment
            // NCLOptionMetadata.Default creates a simple default metadata without NavGlobal access
            if (exprText == "NCLEnumMetadata" && methodName == "Create")
            {
                return SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("NCLOptionMetadata"),
                    SyntaxFactory.IdentifierName("Default"));
            }

            // ALCompiler.ToNavValue(x) -> AlCompat.ToNavValue(x)
            // ToNavValue chains through NavValueFormatter -> NavSession -> NavEnvironment
            if (exprText == "ALCompiler" && methodName == "ToNavValue")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("ToNavValue")));
            }

            // ALCompiler.ObjectToExactNavValue<T>(x) -> (T)(object)x
            if (exprText == "ALCompiler" && methodName == "ObjectToExactNavValue")
            {
                var arg = visited.ArgumentList.Arguments[0].Expression;
                // Extract T from the generic method name
                if (memberAccess.Name is GenericNameSyntax genericName &&
                    genericName.TypeArgumentList.Arguments.Count == 1)
                {
                    var targetType = genericName.TypeArgumentList.Arguments[0];
                    return SyntaxFactory.CastExpression(
                        targetType,
                        SyntaxFactory.ParenthesizedExpression(
                            SyntaxFactory.CastExpression(
                                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword)),
                                SyntaxFactory.ParenthesizedExpression(arg))));
                }
            }

            // ALCompiler.ObjectToDecimal -> AlCompat.ObjectToDecimal
            // ObjectToDecimal accesses NavSession for culture-aware parsing; our version is simpler.
            if (exprText == "ALCompiler" && methodName == "ObjectToDecimal")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("ObjectToDecimal")));
            }

            // ALCompiler.ToInterface(this, codeunit) -> codeunit (strip interface wrapper)
            if (exprText == "ALCompiler" && methodName == "ToInterface")
            {
                var args = visited.ArgumentList.Arguments;
                if (args.Count >= 2)
                    return args[1].Expression;
            }

            // ALCompiler.ObjectToExactINavRecordHandle(x) -> (MockRecordHandle)x
            if (exprText == "ALCompiler" && methodName == "ObjectToExactINavRecordHandle")
            {
                var arg = visited.ArgumentList.Arguments[0].Expression;
                return SyntaxFactory.CastExpression(
                    SyntaxFactory.ParseTypeName("MockRecordHandle"),
                    SyntaxFactory.ParenthesizedExpression(arg));
            }

            // ALCompiler.NavIndirectValueToDecimal(x) -> AlCompat.ObjectToDecimal(x)
            if (exprText == "ALCompiler" && methodName == "NavIndirectValueToDecimal")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("ObjectToDecimal")));
            }

            // ALCompiler.NavIndirectValueToINavRecordHandle(x) -> (MockRecordHandle)x
            if (exprText == "ALCompiler" && methodName == "NavIndirectValueToINavRecordHandle")
            {
                var arg = visited.ArgumentList.Arguments[0].Expression;
                return SyntaxFactory.CastExpression(
                    SyntaxFactory.ParseTypeName("MockRecordHandle"),
                    SyntaxFactory.ParenthesizedExpression(arg));
            }

            // ALCompiler.ToVariant(this, value) -> AlCompat.ToVariant(value)
            // ALCompiler.NavValueToVariant(this, value) -> AlCompat.ToVariant(value)
            if (exprText == "ALCompiler" && (methodName == "ToVariant" || methodName == "NavValueToVariant"))
            {
                var args = visited.ArgumentList.Arguments;
                // Skip the first 'this' argument (ITreeObject)
                if (args.Count >= 2)
                {
                    var valueArg = args[1];
                    return SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("AlCompat"),
                            SyntaxFactory.IdentifierName("ToVariant")),
                        SyntaxFactory.ArgumentList(
                            SyntaxFactory.SingletonSeparatedList(valueArg)));
                }
            }

            // ALCompiler.ObjectToBoolean(x) -> AlCompat.ObjectToBoolean(x)
            if (exprText == "ALCompiler" && methodName == "ObjectToBoolean")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("ObjectToBoolean")));
            }

            // value.ALByValue(this) -> value  (strip ITreeObject calls)
            // value.ModifyLength(N) -> value  (strip length modification)
            if (StripITreeObjectArgMethods.Contains(methodName))
            {
                // Return just the expression the method is called on
                return memberAccess.Expression;
            }

            // NavFormatEvaluateHelper.Format(this.Session, value) -> AlCompat.Format(value)
            if (exprText == "NavFormatEvaluateHelper" && methodName == "Format")
            {
                var args = visited.ArgumentList.Arguments;
                // Skip the first 'this.Session' argument
                if (args.Count >= 2)
                {
                    var keptArgs = new SeparatedSyntaxList<ArgumentSyntax>();
                    for (int i = 1; i < args.Count; i++)
                        keptArgs = keptArgs.Add(args[i]);
                    return SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("AlCompat"),
                            SyntaxFactory.IdentifierName("Format")),
                        SyntaxFactory.ArgumentList(keptArgs));
                }
            }

            // ALSystemErrorHandling.ALClearLastError() -> AlScope.LastErrorText = ""
            // ALSystemErrorHandling.ALGetLastErrorTextFunc(...) -> AlScope.LastErrorText
            if (exprText == "ALSystemErrorHandling")
            {
                if (methodName == "ALClearLastError")
                {
                    // Return an assignment expression: AlScope.LastErrorText = ""
                    return SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("AlScope"),
                            SyntaxFactory.IdentifierName("LastErrorText")),
                        SyntaxFactory.LiteralExpression(
                            SyntaxKind.StringLiteralExpression,
                            SyntaxFactory.Literal("")));
                }

                if (methodName == "ALGetLastErrorTextFunc")
                {
                    // Return just AlScope.LastErrorText (ignore the excludeCustomerData arg)
                    return SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlScope"),
                        SyntaxFactory.IdentifierName("LastErrorText"));
                }
            }
        }

        return visited;
    }

    // -----------------------------------------------------------------------
    // Member access expressions: remove .Target., rewrite base.Parent._parent
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        var visited = (MemberAccessExpressionSyntax)base.VisitMemberAccessExpression(node)!;

        // Pattern: base.Parent.xxx -> _parent.xxx
        // After recursion, base.Parent is a MemberAccessExpression where:
        //   Expression = BaseExpression ("base"), Name = "Parent"
        // So base.Parent.field shows up as: (base.Parent).field
        if (visited.Expression is MemberAccessExpressionSyntax innerAccess2 &&
            innerAccess2.Name.Identifier.Text == "Parent" &&
            innerAccess2.Expression is BaseExpressionSyntax)
        {
            // Replace base.Parent.xxx with _parent.xxx
            return visited.WithExpression(SyntaxFactory.IdentifierName("_parent"));
        }

        // Pattern: xxx.Target -> xxx (strip .Target accessor on handles)
        // This covers both xxx.Target.Method (already handled) and standalone xxx.Target
        if (visited.Name.Identifier.Text == "Target")
        {
            // Just strip .Target and return the expression
            return visited.Expression;
        }

        // (NavOptionMetadata access left as-is — NavOption type is preserved)

        // Pattern: xxx.Target.MethodName -> xxx.MethodName (legacy — now redundant but kept for safety)
        if (visited.Expression is MemberAccessExpressionSyntax innerAccess &&
            innerAccess.Name.Identifier.Text == "Target")
        {
            var outerMethodName = visited.Name.Identifier.Text;

            // Record target methods
            if (RecordTargetMethods.Contains(outerMethodName))
            {
                return visited.WithExpression(innerAccess.Expression);
            }

            // Codeunit target method: Invoke
            if (outerMethodName == "Invoke")
            {
                return visited.WithExpression(innerAccess.Expression);
            }

            // Also handle ToDecimal, and other methods that may chain after Target
            // e.g. this.spikeItem.Target.GetFieldValueSafe(3, NavType.Decimal).ToDecimal()
            // The GetFieldValueSafe is caught above; ToDecimal chains on its result, not on Target.
        }

        // Pattern: ALSystemErrorHandling.ALGetLastErrorText -> AlScope.LastErrorText
        // ALSystemErrorHandling accesses NavCurrentThread.Session which is null in standalone mode.
        if (visited.Expression is IdentifierNameSyntax errHandlingId &&
            errHandlingId.Identifier.Text == "ALSystemErrorHandling")
        {
            var errProp = visited.Name.Identifier.Text;
            if (errProp == "ALGetLastErrorText" || errProp == "ALGetLastErrorCode" || errProp == "ALGetLastErrorCallStack")
            {
                return SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("AlScope"),
                    SyntaxFactory.IdentifierName("LastErrorText"));
            }
        }

        // Pattern: value.ALIsBoolean, value.ALIsText, etc. (NavVariant type-check properties)
        // Rewrite to: AlCompat.ALIsBoolean(value) invocation
        var memberName = visited.Name.Identifier.Text;

        // Pattern: xxx.Session.IsEventSessionRecorderEnabled -> false
        // Also: xxx.Session -> null! (Session property removed with base class)
        if (memberName == "Session" &&
            (visited.Expression is ThisExpressionSyntax || visited.Expression is IdentifierNameSyntax))
        {
            return SyntaxFactory.PostfixUnaryExpression(
                SyntaxKind.SuppressNullableWarningExpression,
                SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression))
                .WithTriviaFrom(visited);
        }
        if (memberName == "IsEventSessionRecorderEnabled")
        {
            return SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression)
                .WithTriviaFrom(visited);
        }
        if (memberName.StartsWith("ALIs") && NavVariantTypeCheckProps.Contains(memberName))
        {
            return SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("AlCompat"),
                    SyntaxFactory.IdentifierName(memberName)),
                SyntaxFactory.ArgumentList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(visited.Expression))));
        }

        return visited;
    }

    private static readonly HashSet<string> NavVariantTypeCheckProps = new(StringComparer.Ordinal)
    {
        "ALIsBoolean", "ALIsOption", "ALIsInteger", "ALIsByte", "ALIsBigInteger",
        "ALIsDecimal", "ALIsText", "ALIsCode", "ALIsChar", "ALIsTextConst",
        "ALIsDate", "ALIsTime", "ALIsDuration", "ALIsDateTime", "ALIsDateFormula",
        "ALIsGuid", "ALIsRecordId", "ALIsRecord", "ALIsRecordRef", "ALIsFieldRef",
        "ALIsCodeunit", "ALIsFile",
    };

    // -----------------------------------------------------------------------
    // Expression statements: remove StmtHit(N); and handle NavRuntimeHelpers
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        // Remove StmtHit(N); statements entirely
        if (node.Expression is InvocationExpressionSyntax invocation)
        {
            if (invocation.Expression is IdentifierNameSyntax ident &&
                ident.Identifier.Text == "StmtHit")
            {
                return null; // remove the statement
            }

            // Remove calls to BC-only methods (ALGetTable, ALClose, RunEvent) that can't work standalone
            if (invocation.Expression is MemberAccessExpressionSyntax stripMa &&
                StripEntireCallMethods.Contains(stripMa.Name.Identifier.Text))
            {
                // Return empty statement instead of null to avoid crash inside using blocks
                return SyntaxFactory.EmptyStatement();
            }

            // NavRuntimeHelpers.CompilationError(...) -> throw new InvalidOperationException("Compilation error");
            if (invocation.Expression is MemberAccessExpressionSyntax ma &&
                ma.Expression.ToString() == "NavRuntimeHelpers" &&
                ma.Name.Identifier.Text == "CompilationError")
            {
                return SyntaxFactory.ThrowStatement(
                    SyntaxFactory.ObjectCreationExpression(
                        SyntaxFactory.ParseTypeName("InvalidOperationException"))
                        .WithArgumentList(SyntaxFactory.ArgumentList(
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.Argument(
                                    SyntaxFactory.LiteralExpression(
                                        SyntaxKind.StringLiteralExpression,
                                        SyntaxFactory.Literal("Compilation error")))))));
            }
        }

        var visited = base.VisitExpressionStatement(node);
        // After visiting children, CStmtHit(N) becomes `true;` which is not a valid statement
        if (visited is ExpressionStatementSyntax exprStmt &&
            exprStmt.Expression is LiteralExpressionSyntax literal &&
            literal.Kind() == SyntaxKind.TrueLiteralExpression)
        {
            return null; // remove the dead statement
        }
        return visited;
    }
}
