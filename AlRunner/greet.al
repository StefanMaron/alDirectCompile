codeunit 50102 "Greeter"
{
    trigger OnRun()
    var
        name: Text[50];
        greeting: Text[100];
    begin
        name := 'AL Developer';
        greeting := 'Welcome, ' + name + '!';
        Message(greeting);
    end;
}
