using Microsoft.EntityFrameworkCore.Storage;

internal static class TransactionHelper
{
    // Rolls back the transaction and always returns false.
    // Used in `catch (Exception) when (await TransactionHelper.RollbackAsync(tx))` so
    // the rollback executes as a side effect while the original exception propagate
    internal static async Task<bool> RollbackAsync(IDbContextTransaction tx)
    {
        try{await tx.RollbackAsync();} catch{ /* swallow — original exception takes priority */ }

        return false;
    }
}