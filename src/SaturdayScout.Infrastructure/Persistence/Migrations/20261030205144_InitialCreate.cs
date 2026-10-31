using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SaturdayScout.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Teams",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Sport = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Abbreviation = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Provider = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Teams", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ExternalSubjectId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Games",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Sport = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Season = table.Column<int>(type: "integer", nullable: false),
                    Week = table.Column<int>(type: "integer", nullable: false),
                    HomeTeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    AwayTeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    KickoffUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    HomeScore = table.Column<int>(type: "integer", nullable: true),
                    AwayScore = table.Column<int>(type: "integer", nullable: true),
                    Provider = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Games", x => x.Id);
                    table.CheckConstraint("CK_Games_Scores", "(\"Status\" = 'Final' AND \"HomeScore\" IS NOT NULL AND \"AwayScore\" IS NOT NULL AND \"HomeScore\" >= 0 AND \"AwayScore\" >= 0) OR (\"Status\" <> 'Final' AND \"HomeScore\" IS NULL AND \"AwayScore\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_Games_Teams_AwayTeamId",
                        column: x => x.AwayTeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Games_Teams_HomeTeamId",
                        column: x => x.HomeTeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Leagues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CommissionerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    InviteCodeHash = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    InviteExpiresUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Leagues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Leagues_Users_CommissionerUserId",
                        column: x => x.CommissionerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LeagueMembers",
                columns: table => new
                {
                    LeagueId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    JoinedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeagueMembers", x => new { x.LeagueId, x.UserId });
                    table.ForeignKey(
                        name: "FK_LeagueMembers_Leagues_LeagueId",
                        column: x => x.LeagueId,
                        principalTable: "Leagues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeagueMembers_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WeeklySlates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LeagueId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sport = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Season = table.Column<int>(type: "integer", nullable: false),
                    Week = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LockUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PublishedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeeklySlates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WeeklySlates_Leagues_LeagueId",
                        column: x => x.LeagueId,
                        principalTable: "Leagues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PickSheets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SlateId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SubmittedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PickSheets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PickSheets_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PickSheets_WeeklySlates_SlateId",
                        column: x => x.SlateId,
                        principalTable: "WeeklySlates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SlateGames",
                columns: table => new
                {
                    SlateId = table.Column<Guid>(type: "uuid", nullable: false),
                    GameId = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlateGames", x => new { x.SlateId, x.GameId });
                    table.CheckConstraint("CK_SlateGames_DisplayOrder", "\"DisplayOrder\" BETWEEN 1 AND 10");
                    table.ForeignKey(
                        name: "FK_SlateGames_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SlateGames_WeeklySlates_SlateId",
                        column: x => x.SlateId,
                        principalTable: "WeeklySlates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Picks",
                columns: table => new
                {
                    SheetId = table.Column<Guid>(type: "uuid", nullable: false),
                    GameId = table.Column<Guid>(type: "uuid", nullable: false),
                    SelectedTeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConfidenceRank = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Picks", x => new { x.SheetId, x.GameId });
                    table.CheckConstraint("CK_Picks_ConfidenceRank", "\"ConfidenceRank\" BETWEEN 1 AND 10");
                    table.ForeignKey(
                        name: "FK_Picks_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Picks_PickSheets_SheetId",
                        column: x => x.SheetId,
                        principalTable: "PickSheets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Picks_Teams_SelectedTeamId",
                        column: x => x.SelectedTeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Games_AwayTeamId",
                table: "Games",
                column: "AwayTeamId");

            migrationBuilder.CreateIndex(
                name: "IX_Games_HomeTeamId",
                table: "Games",
                column: "HomeTeamId");

            migrationBuilder.CreateIndex(
                name: "IX_Games_KickoffUtc",
                table: "Games",
                column: "KickoffUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Games_Provider_ExternalId",
                table: "Games",
                columns: new[] { "Provider", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Games_Sport_Season_Week",
                table: "Games",
                columns: new[] { "Sport", "Season", "Week" });

            migrationBuilder.CreateIndex(
                name: "IX_LeagueMembers_UserId",
                table: "LeagueMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Leagues_CommissionerUserId",
                table: "Leagues",
                column: "CommissionerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Picks_GameId",
                table: "Picks",
                column: "GameId");

            migrationBuilder.CreateIndex(
                name: "IX_Picks_SelectedTeamId",
                table: "Picks",
                column: "SelectedTeamId");

            migrationBuilder.CreateIndex(
                name: "IX_Picks_SheetId_ConfidenceRank",
                table: "Picks",
                columns: new[] { "SheetId", "ConfidenceRank" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PickSheets_SlateId_UserId",
                table: "PickSheets",
                columns: new[] { "SlateId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PickSheets_UserId",
                table: "PickSheets",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_SlateGames_GameId",
                table: "SlateGames",
                column: "GameId");

            migrationBuilder.CreateIndex(
                name: "IX_SlateGames_SlateId_DisplayOrder",
                table: "SlateGames",
                columns: new[] { "SlateId", "DisplayOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Teams_Sport_Provider_ExternalId",
                table: "Teams",
                columns: new[] { "Sport", "Provider", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_ExternalSubjectId",
                table: "Users",
                column: "ExternalSubjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WeeklySlates_LeagueId_Sport_Season_Week",
                table: "WeeklySlates",
                columns: new[] { "LeagueId", "Sport", "Season", "Week" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LeagueMembers");

            migrationBuilder.DropTable(
                name: "Picks");

            migrationBuilder.DropTable(
                name: "SlateGames");

            migrationBuilder.DropTable(
                name: "PickSheets");

            migrationBuilder.DropTable(
                name: "Games");

            migrationBuilder.DropTable(
                name: "WeeklySlates");

            migrationBuilder.DropTable(
                name: "Teams");

            migrationBuilder.DropTable(
                name: "Leagues");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
