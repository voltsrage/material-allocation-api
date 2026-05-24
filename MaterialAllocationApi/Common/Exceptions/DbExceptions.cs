using Microsoft.EntityFrameworkCore;

public static class DbExceptions
{
    public static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException?.Message.Contains("23505") == true
        || ex.InnerException?.Message.Contains("unique constraint") == true;
}
