using PaymentRecoveryDemo;

if (args.Contains("--verify"))
{
    Scenarios.Verify();
    return;
}

var directory = Path.Combine(Path.GetTempPath(), "payment-recovery-" + Guid.NewGuid().ToString("N"));
try
{
    var ledger = Path.Combine(directory, "ledger.json");
    var external = Path.Combine(directory, "processor.json");
    var processor = new FakeProcessor(external);
    var service = new PaymentService(ledger, processor);
    var request = new PaymentRequest("demo-payment-1", "synthetic-invoice-1", 12500, "CAD");
    try
    {
        service.Pay(request, () => throw new IOException("Simulated local failure after processor success"));
    }
    catch (IOException)
    {
        Console.WriteLine($"After the failure: local={service.Operations[request.Key].State}, processor charges={processor.ChargeCount}");
    }
    var restarted = new PaymentService(ledger, new FakeProcessor(external));
    Console.WriteLine($"After restart: reconciled={restarted.Reconcile()}, local={restarted.Operations[request.Key].State}, processor charges={processor.ChargeCount}");
    var receipt = restarted.Pay(request);
    Console.WriteLine($"After another retry: receipt={receipt.Id}, processor charges={processor.ChargeCount}");
}
finally
{
    if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
}

public static class Scenarios
{
    public static void Verify()
    {
        var directory = Path.Combine(Path.GetTempPath(), "payment-specs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var ledger = Path.Combine(directory, "ledger.json");
            var external = Path.Combine(directory, "processor.json");
            var processor = new FakeProcessor(external);
            var service = new PaymentService(ledger, processor);
            var request = new PaymentRequest("key-1", "invoice-1", 12500, "CAD");
            Expect<IOException>(() => service.Pay(request, () => throw new IOException("Injected failure")));
            Check(service.Operations[request.Key].State == "Pending", "intent survives local failure");
            Check(processor.ChargeCount == 1, "external action committed once");
            var restarted = new PaymentService(ledger, new FakeProcessor(external));
            Check(restarted.Reconcile() == 1, "pending intent recovered after restart");
            Check(restarted.Operations[request.Key].State == "Succeeded", "ledger reconciled");
            var receipt = restarted.Pay(request);
            Check(processor.ChargeCount == 1, "retry creates no duplicate charge");
            Check(restarted.Pay(request).Id == receipt.Id, "retry preserves receipt identity");
            Expect<InvalidOperationException>(() => restarted.Pay(request with { AmountMinor = 25000 }));
            Check(processor.ChargeCount == 1, "conflicting key creates no charge");
            Expect<ArgumentException>(() => restarted.Pay(request with { Key = "key-invalid", AmountMinor = 0 }));
            Check(!restarted.Operations.ContainsKey("key-invalid"), "invalid request creates no intent");
            Check(restarted.Reconcile() == 0, "completed reconciliation is idempotent");
            var next = request with { Key = "key-2", Invoice = "invoice-2" };
            Check(restarted.Pay(next).Id != receipt.Id && processor.ChargeCount == 2, "distinct payment stays distinct");
            Parallel.For(0, 20, _ => restarted.Pay(next));
            Check(processor.ChargeCount == 2, "concurrent retries in one service are serialized");
            Console.WriteLine("All recovery, conflict, validation and concurrent-retry scenarios passed.");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("Scenario failed: " + name);
        Console.WriteLine("PASS " + name);
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
