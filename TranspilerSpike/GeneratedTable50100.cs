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

    public sealed class Record50100 : NavRecord
    {
        public Record50100(ITreeObject parent, NCLMetaTable metaTable, bool isTemporary, NavRecord sharedTable, string companyName, SecurityFiltering securityFiltering) : base(parent, 50100, metaTable, isTemporary, sharedTable, companyName, securityFiltering)
        {
        }

        public override bool IsCompiledForOnPremise => true;
        private Record50100 Rec => (Record50100)this;
        private Record50100 xRec => (Record50100)this.OldRecord;

        public static Record50100 __Construct(ITreeObject parent, NCLMetaTable metaTable, bool isTemporary, NavRecord sharedTable, string companyName, SecurityFiltering securityFiltering)
        {
            return new Record50100(parent, metaTable, isTemporary, sharedTable, companyName, securityFiltering);
        }
    }
}