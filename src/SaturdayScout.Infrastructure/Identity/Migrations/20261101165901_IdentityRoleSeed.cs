using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace SaturdayScout.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class IdentityRoleSeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                schema: "auth",
                table: "AspNetRoles",
                columns: new[] { "Id", "ConcurrencyStamp", "Name", "NormalizedName" },
                values: new object[,]
                {
                    { new Guid("a98acb10-a901-4d63-a5de-3cad31010a01"), "system-administrator-v1", "SystemAdministrator", "SYSTEMADMINISTRATOR" },
                    { new Guid("a98acb10-a901-4d63-a5de-3cad31010a02"), "league-commissioner-v1", "LeagueCommissioner", "LEAGUECOMMISSIONER" },
                    { new Guid("a98acb10-a901-4d63-a5de-3cad31010a03"), "player-v1", "Player", "PLAYER" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                schema: "auth",
                table: "AspNetRoles",
                keyColumn: "Id",
                keyValue: new Guid("a98acb10-a901-4d63-a5de-3cad31010a01"));

            migrationBuilder.DeleteData(
                schema: "auth",
                table: "AspNetRoles",
                keyColumn: "Id",
                keyValue: new Guid("a98acb10-a901-4d63-a5de-3cad31010a02"));

            migrationBuilder.DeleteData(
                schema: "auth",
                table: "AspNetRoles",
                keyColumn: "Id",
                keyValue: new Guid("a98acb10-a901-4d63-a5de-3cad31010a03"));
        }
    }
}
