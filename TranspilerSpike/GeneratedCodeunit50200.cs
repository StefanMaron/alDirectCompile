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

    [NavCodeunitOptions(0, 0, CodeunitSubType.Test, false)]
    public sealed class Codeunit50200 : NavTestCodeunit
    {
        public Codeunit50200(ITreeObject parent) : base(parent, 50200)
        {
        }

        public override string ObjectName => "Spike Tests";
        public override bool IsCompiledForOnPremise => true;

        protected override object OnInvoke(int memberId, object[] args)
        {
            switch (memberId)
            {
                case 1910870739:
                    if (args.Length != 0)
                        NavRuntimeHelpers.CompilationError(new NavNCLInvalidNumberOfArgumentsException("TestApplyDiscount", 0, args.Length));
                    TestApplyDiscount();
                    break;
                default:
                    NavRuntimeHelpers.CompilationError(Lang.WrongReference, memberId, 50200);
                    break;
            }

            return default;
        }

        public static Codeunit50200 __Construct(ITreeObject parent)
        {
            return new Codeunit50200(parent);
        }

        [NavTest("TestApplyDiscount", TestMethodNo = 663, TestPermissions = NavTestPermissions.Restrictive), NavFunctionVisibility(FunctionVisibility.External)]
        public void TestApplyDiscount()
        {
            using (TestApplyDiscount_Scope_1910870739 \u03b2scope = new TestApplyDiscount_Scope_1910870739(this))
                \u03b2scope.Run();
        }

        [NavName("TestApplyDiscount")]
        [SignatureSpan(1688909990199327L)]
        [SourceSpans(3377734081052697L, 3659209057828899L, 3940684034605101L, 4222159011381286L, 4503633988157472L, 5348058918486064L, 6192483848814623L, 6473971710492711L, 6755450982236225L, 7036904484175880L)]
        private sealed class TestApplyDiscount_Scope_1910870739 : NavMethodScope<Codeunit50200>
        {
            public static uint \u03b1scopeId;
            [NavName("SpikeItem")]
            public INavRecordHandle spikeItem;
            [NavName("SpikeLogic")]
            public NavCodeunitHandle spikeLogic;
            protected override uint RawScopeId { get => TestApplyDiscount_Scope_1910870739.\u03b1scopeId; set => TestApplyDiscount_Scope_1910870739.\u03b1scopeId = value; }

            internal TestApplyDiscount_Scope_1910870739(Codeunit50200 \u03b2parent) : base(\u03b2parent)
            {
                this.spikeItem = new NavRecordHandle(this, 50100, false, SecurityFiltering.Validated);
                this.spikeLogic = new NavCodeunitHandle(this, 50100);
            }

            protected override void OnRun()
            {
                StmtHit(0);
                this.spikeItem.Target.ALInit();
                StmtHit(1);
                this.spikeItem.Target.SetFieldValueSafe(1, NavType.Code, new NavCode(20, "ITEM1"));
                StmtHit(2);
                this.spikeItem.Target.SetFieldValueSafe(2, NavType.Text, new NavText(100, "Test Item"));
                StmtHit(3);
                this.spikeItem.Target.SetFieldValueSafe(3, NavType.Decimal, ALCompiler.ToNavValue(100));
                StmtHit(4);
                this.spikeItem.Target.ALInsert(DataError.ThrowError, false);
                StmtHit(5);
                this.spikeLogic.Target.Invoke(1351223168, new object[] { this.spikeItem, 10 });
                StmtHit(6);
                this.spikeItem.Target.ALGet(DataError.ThrowError, ALCompiler.ToNavValue("ITEM1"));
                if (CStmtHit(7) & (this.spikeItem.Target.GetFieldValueSafe(3, NavType.Decimal).ToDecimal() != 90))
                {
                    StmtHit(8);
                    NavDialog.ALError(this.Session, System.Guid.Parse("8da61efd-0002-0003-0507-0b0d1113171d"), "Expected 90, got %1", this.spikeItem.Target.GetFieldRefSafe(3, NavType.Decimal));
                }
            }
        }
    }
}