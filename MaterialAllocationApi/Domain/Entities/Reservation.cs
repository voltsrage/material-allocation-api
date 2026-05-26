public class Reservation
{
    public Guid Id { get; private set; }
    public Guid OrderLineId {get; private set;}
public int Quantity { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public OrderLine OrderLine { get; private set; } = null!;

    private Reservation() {}

    internal Reservation(Guid orderLineId, int quantity, DateTimeOffset expiresAt)
    {
        OrderLineId = orderLineId;
        Quantity = quantity;
        ExpiresAt = expiresAt;
        CreatedAt = DateTimeOffset.UtcNow;
    }
}