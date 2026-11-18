namespace CashMovements.IntegrationTests;

internal sealed class ExportProgressWriter : StringWriter
{
    private readonly Action<string> observeLine;

    internal ExportProgressWriter(Action<string> observeLine)
    {
        this.observeLine = observeLine;
    }

    public override void WriteLine(string? value)
    {
        base.WriteLine(value);
        if (value is not null)
        {
            observeLine(value);
        }
    }
}
