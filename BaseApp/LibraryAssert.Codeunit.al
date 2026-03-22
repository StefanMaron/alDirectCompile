codeunit 50101 "Library Assert"
{
    procedure AreEqual(Expected: Variant; Actual: Variant; Msg: Text)
    begin
        if Format(Expected) <> Format(Actual) then
            Error('Assert.AreEqual failed. Expected:<%1>, Actual:<%2>. %3', Expected, Actual, Msg);
    end;

    procedure AreNotEqual(Expected: Variant; Actual: Variant; Msg: Text)
    begin
        if Format(Expected) = Format(Actual) then
            Error('Assert.AreNotEqual failed. Expected any value except:<%1>. %2', Expected, Msg);
    end;

    procedure IsTrue(Condition: Boolean; Msg: Text)
    begin
        if not Condition then
            Error('Assert.IsTrue failed. %1', Msg);
    end;

    procedure IsFalse(Condition: Boolean; Msg: Text)
    begin
        if Condition then
            Error('Assert.IsFalse failed. %1', Msg);
    end;
}
