
public class ReservationExpiryJob : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReservationExpiryJob> _logger;
    private readonly TimeSpan _interval;

    public ReservationExpiryJob(
        IServiceScopeFactory scopeFactory, 
        ILogger<ReservationExpiryJob> logger,
        IConfiguration config)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _interval = TimeSpan.FromSeconds(
            config.GetValue("ReservationExpiry:IntervalSeconds", 60)
        );
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Reservation expiry job started. Interval: {Interval}s.", _interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_interval, stoppingToken);
            
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<IReservationService>();
                await svc.ExpireAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {                
                _logger.LogError(ex, "Reservation expiry job encountered an error.");
            }
        }
    }
}