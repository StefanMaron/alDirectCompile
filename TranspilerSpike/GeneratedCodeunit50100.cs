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
                case 1351223168:
                    if (args.Length != 2)
                        NavRuntimeHelpers.CompilationError(new NavNCLInvalidNumberOfArgumentsException("ApplyDiscount", 2, args.Length));
                    ApplyDiscount(ALCompiler.ObjectToExactINavRecordHandle(args[0]), (Decimal18)ALCompiler.ObjectToDecimal(args[1]));
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

        [NavFunctionVisibility(FunctionVisibility.External), NavCaption(TranslationKey = "Codeunit 3678372286 - Method 4246314982")]
        public void ApplyDiscount([NavObjectId(ObjectId = 50100)][NavByReferenceAttribute] INavRecordHandle spikeItem, Decimal18 pct)
        {
            using (ApplyDiscount_Scope_1351223168 \u03b2scope = new ApplyDiscount_Scope_1351223168(this, spikeItem, pct))
                \u03b2scope.Run();
        }

        [NavName("ApplyDiscount")]
        [SignatureSpan(844485059870747L)]
        [SourceSpans(1407409243619403L, 1688884220395552L, 1970354902204424L)]
        private sealed class ApplyDiscount_Scope_1351223168 : NavMethodScope<Codeunit50100>
        {
            public static uint \u03b1scopeId;
            [NavName("SpikeItem")]
            public INavRecordHandle spikeItem;
            [NavName("Pct")]
            public Decimal18 pct;
            protected override uint RawScopeId { get => ApplyDiscount_Scope_1351223168.\u03b1scopeId; set => ApplyDiscount_Scope_1351223168.\u03b1scopeId = value; }

            internal ApplyDiscount_Scope_1351223168(Codeunit50100 \u03b2parent, [NavObjectId(ObjectId = 50100)][NavByReferenceAttribute] INavRecordHandle spikeItem, Decimal18 pct) : base(\u03b2parent)
            {
                this.spikeItem = spikeItem;
                this.pct = pct;
            }

            protected override void OnRun()
            {
                StmtHit(0);
                this.spikeItem.Target.SetFieldValueSafe(3, NavType.Decimal, ALCompiler.ToNavValue(this.spikeItem.Target.GetFieldValueSafe(3, NavType.Decimal).ToDecimal() * (1 - this.pct / ((Decimal18)100))));
                StmtHit(1);
                this.spikeItem.Target.ALModify(DataError.ThrowError, false);
            }
        }
    }
}