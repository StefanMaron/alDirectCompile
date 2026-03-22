namespace Microsoft.Dynamics.Nav.BusinessApplication
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Microsoft.Dynamics.Nav.Common.Language;
    using Microsoft.Dynamics.Nav.EventSubscription;
    using Microsoft.Dynamics.Nav.Runtime;
    using Microsoft.Dynamics.Nav.Runtime.Extensions;
    using Microsoft.Dynamics.Nav.Runtime.Report;
    using Microsoft.Dynamics.Nav.Types;
    using Microsoft.Dynamics.Nav.Types.Exceptions;
    using Microsoft.Dynamics.Nav.Types.Metadata;

    [NavCodeunitOptions(0, 0, CodeunitSubType.Normal, false)]
    public sealed class Codeunit50100 : NavCodeunit
    {
        public Codeunit50100(ITreeObject parent) : base(parent, 50100)
        {
        }

        public override string ObjectName => "Spike Logic";
        public override bool IsCompiledForOnPremise => true;

        protected override object OnInvoke(int memberId, object[] args)
        {
            switch (memberId)
            {
                case -743102751:
                    if (args.Length != 2)
                        NavRuntimeHelpers.CompilationError(new NavNCLInvalidNumberOfArgumentsException("ApplyDiscount", 2, args.Length));
                    ApplyDiscount((ByRef<Decimal18>)ALCompiler.SafeCastCheck<ByRef<Decimal18>>(args[0]), (Decimal18)ALCompiler.ObjectToDecimal(args[1]));
                    break;
                case 376357172:
                    if (args.Length != 1)
                        NavRuntimeHelpers.CompilationError(new NavNCLInvalidNumberOfArgumentsException("Greet", 1, args.Length));
                    return Greet(ALCompiler.ObjectToExactNavValue<NavText>(args[0]));
                    break;
                default:
                    NavRuntimeHelpers.CompilationError(Lang.WrongReference, memberId, 50100);
                    break;
            }

            return default;
        }

        public static Codeunit50100 __Construct(ITreeObject parent)
        {
            return new Codeunit50100(parent);
        }

        [NavFunctionVisibility(FunctionVisibility.External), NavCaption(TranslationKey = "Codeunit 3678372286 - Method 2668645111")]
        public void ApplyDiscount(ByRef<Decimal18> unitPrice, Decimal18 pct)
        {
            using (ApplyDiscount_Scope__743102751 \u03b2scope = new ApplyDiscount_Scope__743102751(this, unitPrice, pct))
                \u03b2scope.Run();
        }

        [NavName("ApplyDiscount")]
        [SignatureSpan(844485059870747L)]
        [SourceSpans(1407409243619377L, 1688879925428232L)]
        private sealed class ApplyDiscount_Scope__743102751 : NavMethodScope<Codeunit50100>
        {
            public static uint \u03b1scopeId;
            [NavName("UnitPrice")]
            public ByRef<Decimal18> unitPrice;
            [NavName("Pct")]
            public Decimal18 pct;
            protected override uint RawScopeId { get => ApplyDiscount_Scope__743102751.\u03b1scopeId; set => ApplyDiscount_Scope__743102751.\u03b1scopeId = value; }

            internal ApplyDiscount_Scope__743102751(Codeunit50100 \u03b2parent, ByRef<Decimal18> unitPrice, Decimal18 pct) : base(\u03b2parent)
            {
                this.unitPrice = unitPrice;
                this.pct = pct;
            }

            protected override void OnRun()
            {
                StmtHit(0);
                this.unitPrice.Value = this.unitPrice.Value * (1 - this.pct / ((Decimal18)100));
            }
        }

        [NavFunctionVisibility(FunctionVisibility.External), NavCaption(TranslationKey = "Codeunit 3678372286 - Method 1769785633")]
        public NavText Greet(NavText name)
        {
            using (Greet_Scope_376357172 \u03b2scope = new Greet_Scope_376357172(this, name))
            {
                \u03b2scope.Run();
                return \u03b2scope.\u03b3retVal;
            }
        }

        [NavName("Greet")]
        [SignatureSpan(2251859943751699L)]
        [SourceSpans(2814784127500325L, 3096254809309192L)]
        private sealed class Greet_Scope_376357172 : NavMethodScope<Codeunit50100>
        {
            public static uint \u03b1scopeId;
            [NavName("Name")]
            public NavText name;
            [ReturnValue]
            public NavText \u03b3retVal = NavText.Default(0);
            protected override uint RawScopeId { get => Greet_Scope_376357172.\u03b1scopeId; set => Greet_Scope_376357172.\u03b1scopeId = value; }

            internal Greet_Scope_376357172(Codeunit50100 \u03b2parent, NavText name) : base(\u03b2parent)
            {
                this.name = name.ModifyLength(0);
            }

            protected override void OnRun()
            {
                StmtHit(0);
                this.\u03b3retVal = new NavText("Hello, " + this.name + "!");
                return;
            }
        }
    }
}