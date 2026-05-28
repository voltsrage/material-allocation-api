using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

[ApiController]
[Route("api/v1/customers")]
public class CustomersController : ControllerBase
{
    private readonly ICustomerService _customers;

    public CustomersController(ICustomerService customers)
    {
        _customers = customers;
    }

    /// <summary>Register a new customer account.</summary>
    /// <response code="201">Customer created.</response>
    /// <response code="422">Customer code already exists or required field missing.</response>
    [HttpPost]
    [Authorize(Roles = "sales-ops")]
    [ProducesResponseType(typeof(ApiResponse<CustomerResponse>), 201)]
    [ProducesResponseType(typeof(ApiResponse<object>), 422)]
    public async Task<ActionResult<ApiResponse<CustomerResponse>>> Create(CreateCustomerRequest request, CancellationToken ct)
    {
        var customer = await _customers.CreateAsync(request, ct);
        return CreatedAtAction(nameof(GetById), new {id = customer.Id}, ApiResponse<CustomerResponse>.Created(customer));
    }

    /// <summary>Get a customer by ID.</summary>
    /// <response code="200">Customer found.</response>
    /// <response code="404">No customer with the given ID exists.</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse<CustomerResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 404)]
    public async Task<ActionResult<ApiResponse<CustomerResponse>>> GetById(Guid id, CancellationToken ct)
    {
        var customer = await _customers.GetByIdAsync(id, ct);
        return Ok(ApiResponse<CustomerResponse>.Ok(customer));
    }

    /// <summary>List customers with pagination, ordered by customer code.</summary>
    /// <response code="200">Paginated customer list.</response>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<CustomerResponse>>), 200)]
    public async Task<ActionResult<ApiResponse<PagedResult<CustomerResponse>>>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default
    )
    {
        var result = await _customers.ListAsync(page, pageSize, ct);
        return Ok(ApiResponse<PagedResult<CustomerResponse>>.Ok(result));
    }
}