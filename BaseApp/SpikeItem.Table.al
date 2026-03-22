table 50100 "Spike Item"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20])
        {
        }
        field(2; Description; Text[100])
        {
        }
        field(3; "Unit Price"; Decimal)
        {
        }
    }

    keys
    {
        key(PK; "No.")
        {
            Clustered = true;
        }
    }
}
