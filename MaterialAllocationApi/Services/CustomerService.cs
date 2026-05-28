
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;

public class CustomerService : ICustomerService
{
    private readonly AllocationDbContext _db;
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<CustomerService> _logger;

    public CustomerService(AllocationDbContext db, IDbConnectionFactory connectionFactory, ILogger<CustomerService> logger)
    {
        _db = db;
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<CustomerResponse> CreateAsync(CreateCustomerRequest request, CancellationToken ct = default)
    {
        var customer = new Customer(request.CustomerCode, request.Name, request.Tier);
        _db.Customers.Add(customer);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex))
        {
            throw new ValidationException($"Customer code '{request.CustomerCode}' already exists.");
        }

        _logger.LogInformation("Customer {CustomerId} created: code={Code}, tier={Tier}.",
        customer.Id, customer.CustomerCode, customer.Tier.ToDbString());

        return  ToResponse(customer);
    }

    public async Task<CustomerResponse> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = await _connectionFactory.CreateAsync(ct);
        
        var row = await conn.QueryFirstOrDefaultAsync<CustomerResponse>(
            @"
            SELECT
                id as Id,
                customer_code as CustomerCode,
                name as Name,
                tier as Tier,
                created_at as CreatedAt
            FROM customers
            WHERE id = @id
            ",
            new {id}
        );

        return row ?? throw new NotFoundException($"Customer {id} not found.");
    }

    public async Task<PagedResult<CustomerResponse>> ListAsync(int page, int pageSize, CancellationToken ct = default)
    {
        pageSize = Math.Min(pageSize, 100);
        page = Math.Max(page, 1);
        var offset = (page - 1) * pageSize;

        using var conn = await _connectionFactory.CreateAsync(ct);

        var total = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM customers");

        var items =(await conn.QueryAsync<CustomerResponse>(
            @"
            SELECT
                id as Id,
                customer_code as CustomerCode,
                name as Name,
                tier AS Tier,
                created_at AS CreatedAt
            FROM customers
            ORDER BY customer_code
            LIMIT @pageSize OFFSET @offset
            ",
            new {pageSize, offset}
        )).AsList();

        return new PagedResult<CustomerResponse>(items, page, pageSize, total);
    }    

    private static CustomerResponse ToResponse(Customer c) =>
        new(c.Id, c.CustomerCode, c.Name, c.Tier.ToDbString(), c.CreatedAt.UtcDateTime);
}