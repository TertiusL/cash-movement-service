namespace CashMovements.StatementClient;

internal sealed record StatementClientOptions(Uri Endpoint, ExportStatementRequest Request, string OutputPath)
{
    internal const string Usage =
        "Usage: --endpoint <http(s)://host:port> --account <accountId> --from <inclusive timestamp> " +
        "--to <exclusive timestamp> --output <filename>";

    internal static StatementClientOptions Parse(string[] arguments)
    {
        Dictionary<string, string> options = new(StringComparer.Ordinal);
        for (int index = 0; index < arguments.Length; index += 2)
        {
            string name = arguments[index];
            if (name is not ("--endpoint" or "--account" or "--from" or "--to" or "--output"))
            {
                throw new ArgumentException($"Unknown option '{name}'.");
            }

            if (index + 1 >= arguments.Length || !options.TryAdd(name, arguments[index + 1]))
            {
                throw new ArgumentException($"Supply {name} once with a value.");
            }
        }

        string Required(string name)
        {
            if (!options.TryGetValue(name, out string? value) || string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException($"Missing value for {name}.");
            }

            return value;
        }

        if (!Uri.TryCreate(Required("--endpoint"), UriKind.Absolute, out Uri? endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Supply an absolute HTTP or HTTPS address for --endpoint.");
        }

        ExportStatementRequest request = new()
        {
            AccountId = Required("--account"),
            FromInclusive = ParseTimestamp(Required("--from"), "--from"),
            ToExclusive = ParseTimestamp(Required("--to"), "--to")
        };
        return new StatementClientOptions(endpoint, request, Required("--output"));
    }

    private static Timestamp ParseTimestamp(string value, string option)
    {
        // Protobuf parses an explicit offset and retains all nine fractional digits for the precision check.
        Timestamp timestamp;
        try
        {
            timestamp = Timestamp.Parser.ParseJson(JsonSerializer.Serialize(value));
        }
        catch (InvalidProtocolBufferException)
        {
            throw new ArgumentException($"Supply an RFC 3339 timestamp with Z or an explicit offset for {option}.");
        }

        if (timestamp.Nanos % 100 != 0)
        {
            throw new ArgumentException($"{option} must be exactly representable in 100 ns ticks.");
        }

        return timestamp;
    }
}
