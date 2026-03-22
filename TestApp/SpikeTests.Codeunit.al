codeunit 50200 "Spike Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "Library Assert";

    [Test]
    procedure TestApplyDiscount()
    var
        SpikeItem: Record "Spike Item";
        SpikeLogic: Codeunit "Spike Logic";
    begin
        // Setup
        SpikeItem.Init();
        SpikeItem."No." := 'ITEM1';
        SpikeItem.Description := 'Test Item';
        SpikeItem."Unit Price" := 100;
        SpikeItem.Insert(false);

        // Exercise
        SpikeLogic.ApplyDiscount(SpikeItem, 10);

        // Verify
        SpikeItem.Get('ITEM1');
        Assert.AreEqual(90, SpikeItem."Unit Price", 'Discount should reduce price to 90');
    end;

    [Test]
    procedure TestDoublePrice()
    var
        SpikeItem: Record "Spike Item";
        SpikeLogic: Codeunit "Spike Logic";
    begin
        // Setup
        SpikeItem.Init();
        SpikeItem."No." := 'ITEM2';
        SpikeItem."Unit Price" := 50;
        SpikeItem.Insert(false);

        // Exercise
        SpikeLogic.DoublePrice(SpikeItem);

        // Verify
        SpikeItem.Get('ITEM2');
        Assert.AreEqual(100, SpikeItem."Unit Price", 'Double price should give 100');
    end;

    [Test]
    procedure TestDiscountValidation()
    var
        SpikeItem: Record "Spike Item";
        SpikeLogic: Codeunit "Spike Logic";
    begin
        // Setup
        SpikeItem.Init();
        SpikeItem."No." := 'ITEM3';
        SpikeItem."Unit Price" := 200;
        SpikeItem.Insert(false);

        // Exercise - apply 50% discount
        SpikeLogic.ApplyDiscount(SpikeItem, 50);

        // Verify
        SpikeItem.Get('ITEM3');
        Assert.AreEqual(100, SpikeItem."Unit Price", '50% discount on 200 should give 100');
        Assert.AreNotEqual(200, SpikeItem."Unit Price", 'Price should have changed');
        Assert.IsTrue(SpikeItem."Unit Price" > 0, 'Price should be positive');
    end;
}
