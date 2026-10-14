using System.Text.Json;

namespace PaymentRecoveryDemo;

public record PaymentRequest(string Key, string Invoice, long AmountMinor, string Currency);
public record Receipt(string Id, PaymentRequest Request);
public record Operation(PaymentRequest Request, string State, Receipt? Receipt = null);

// Each file models a separate system. There is no shared transaction between them.
public sealed class JsonStore<T>(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public Dictionary<string, T> Read() => File.Exists(path)
        ? JsonSerializer.Deserialize<Dictionary<string, T>>(File.ReadAllText(path), Options)
          ?? throw new InvalidDataException("Stored state is invalid")
        : [];

    public void Write(Dictionary<string, T> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                JsonSerializer.Serialize(stream, rows, Options);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

// A synthetic processor with durable idempotency, independent of the local ledger.
public sealed class FakeProcessor(string path)
{
    private readonly JsonStore<Receipt> store = new(path);
    public int ChargeCount => store.Read().Count;

    public Receipt Charge(PaymentRequest request)
    {
        var receipts = store.Read();
        if (receipts.TryGetValue(request.Key, out var existing))
        {
            if (existing.Request != request)
                throw new InvalidOperationException("Processor idempotency key has a different payload");
            return existing;
        }
        var receipt = new Receipt("demo_" + Guid.NewGuid().ToString("N"), request);
        receipts.Add(request.Key, receipt);
        store.Write(receipts);
        return receipt;
    }
}

public sealed class PaymentService(string path, FakeProcessor processor)
{
    private readonly JsonStore<Operation> store = new(path);
    private readonly object gate = new();
    public IReadOnlyDictionary<string, Operation> Operations => store.Read();

    public Receipt Pay(PaymentRequest request, Action? afterProcessorCommit = null)
    {
        if (string.IsNullOrWhiteSpace(request.Key) || request.Key.Length > 100
            || string.IsNullOrWhiteSpace(request.Invoice) || request.AmountMinor <= 0
            || request.Currency.Length != 3 || request.Currency.Any(c => c is < 'A' or > 'Z'))
            throw new ArgumentException("A key, invoice, positive minor-unit amount and uppercase currency are required");
        lock (gate)
        {
            var operations = store.Read();
            if (operations.TryGetValue(request.Key, out var operation))
            {
                if (operation.Request != request)
                    throw new InvalidOperationException("Local idempotency key has a different payload");
                if (operation.State == "Succeeded")
                    return operation.Receipt ?? throw new InvalidDataException("Succeeded operation lacks a receipt");
            }
            else
            {
                // Commit the intent before crossing the external system boundary.
                operations.Add(request.Key, new Operation(request, "Pending"));
                store.Write(operations);
            }
            var receipt = processor.Charge(request);
            // Failure injection models processor success followed by a local failure.
            afterProcessorCommit?.Invoke();
            operations[request.Key] = new Operation(request, "Succeeded", receipt);
            store.Write(operations);
            return receipt;
        }
    }

    public int Reconcile()
    {
        lock (gate)
        {
            var pending = store.Read().Values.Where(row => row.State == "Pending").ToArray();
            foreach (var row in pending) Pay(row.Request);
            return pending.Length;
        }
    }
}
