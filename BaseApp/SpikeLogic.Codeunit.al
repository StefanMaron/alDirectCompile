codeunit 50100 "Spike Logic"
{
    procedure ApplyDiscount(var SpikeItem: Record "Spike Item"; Pct: Decimal)
    begin
        SpikeItem."Unit Price" := SpikeItem."Unit Price" * (1 - Pct / 100);
        SpikeItem.Modify(false);
    end;

    procedure DoublePrice(var SpikeItem: Record "Spike Item")
    begin
        SpikeItem."Unit Price" := SpikeItem."Unit Price" * 2;
        SpikeItem.Modify(false);
    end;
}
