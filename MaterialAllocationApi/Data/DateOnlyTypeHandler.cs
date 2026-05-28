using System.Data;
using Dapper;

// Bridges Dapper's type mapping for DateOnly, which Npgsql returns as DateTime
// from 'timestamp without time zone' columns that were created before the type
// was changed to 'date'. Without this, Dapper throws InvalidCastException.
public sealed class DateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly>
{
    public override DateOnly Parse(object value) =>
        DateOnly.FromDateTime((DateTime)value);

    public override void SetValue(IDbDataParameter parameter, DateOnly value)
    {
        parameter.DbType = DbType.Date;
        parameter.Value  = value.ToDateTime(TimeOnly.MinValue);
    }
}
