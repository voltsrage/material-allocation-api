public class Customer
{
    public Guid Id { get; private set; }
    public string CustomerCode { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public CustomerTier Tier { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private Customer() {}

    public Customer(string customerCode, string name, CustomerTier tier)
    {
        CustomerCode = customerCode;
        Name = name;
        Tier = tier;
        CreatedAt = DateTimeOffset.UtcNow;
    }
}