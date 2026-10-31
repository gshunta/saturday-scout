using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SaturdayScout.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CollegeFootballDataTraceAndUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SourceRetrievedUtc",
                table: "Games",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceStatus",
                table: "Games",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProviderRequestUsages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Endpoint = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    RequestedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    HttpStatus = table.Column<int>(type: "integer", nullable: true),
                    RemainingReported = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderRequestUsages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderRequestUsages_Provider_RequestedUtc",
                table: "ProviderRequestUsages",
                columns: new[] { "Provider", "RequestedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProviderRequestUsages");

            migrationBuilder.DropColumn(
                name: "SourceRetrievedUtc",
                table: "Games");

            migrationBuilder.DropColumn(
                name: "SourceStatus",
                table: "Games");
        }
    }
}
