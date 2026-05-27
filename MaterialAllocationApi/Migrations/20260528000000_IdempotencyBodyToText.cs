using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MaterialAllocationApi.Migrations
{
    /// <inheritdoc />
    public partial class IdempotencyBodyToText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // JSONB normalizes key order, which breaks byte-for-byte idempotency replay.
            // Storing the response body as text preserves the exact bytes written by the
            // controller so the replayed response is identical to the original response.
            migrationBuilder.AlterColumn<string>(
                name: "response_body",
                table: "idempotency_keys",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "jsonb",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "response_body",
                table: "idempotency_keys",
                type: "jsonb",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
