using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Discagem.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRooms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Rooms",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    OwnerId = table.Column<long>(type: "INTEGER", nullable: true),
                    Color = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    HasIcon = table.Column<bool>(type: "INTEGER", nullable: false),
                    IconVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    InviteCode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rooms", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Rooms_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "RoomMembers",
                columns: table => new
                {
                    RoomId = table.Column<long>(type: "INTEGER", nullable: false),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    JoinedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoomMembers", x => new { x.RoomId, x.UserId });
                    table.ForeignKey(
                        name: "FK_RoomMembers_Rooms_RoomId",
                        column: x => x.RoomId,
                        principalTable: "Rooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RoomMembers_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // The server's existing channels become the room "Discagem", owned by the first administrator,
            // with every existing account as a member. A fresh database has only the voice channel seeded
            // by an earlier migration and no accounts: DatabaseInitializer removes that memberless room.
            migrationBuilder.Sql("""
                INSERT INTO "Rooms" ("Id", "Name", "OwnerId", "Color", "HasIcon", "IconVersion", "InviteCode", "CreatedAt")
                SELECT 1, 'Discagem',
                       (SELECT "Id" FROM "Users" ORDER BY "IsAdmin" DESC, "Id" LIMIT 1),
                       '#5865F2', 0, 0, upper(substr(hex(randomblob(4)), 1, 8)),
                       (SELECT MIN("CreatedAt") FROM "Channels")
                WHERE EXISTS (SELECT 1 FROM "Channels");
                """);
            migrationBuilder.Sql("""
                INSERT INTO "RoomMembers" ("RoomId", "UserId", "JoinedAt")
                SELECT 1, "Id", "CreatedAt" FROM "Users"
                WHERE EXISTS (SELECT 1 FROM "Rooms" WHERE "Id" = 1);
                """);

            migrationBuilder.AddColumn<long>(
                name: "RoomId",
                table: "Channels",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.Sql("""UPDATE "Channels" SET "RoomId" = 1;""");

            migrationBuilder.CreateIndex(
                name: "IX_Channels_RoomId",
                table: "Channels",
                column: "RoomId");

            migrationBuilder.CreateIndex(
                name: "IX_RoomMembers_UserId",
                table: "RoomMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Rooms_InviteCode",
                table: "Rooms",
                column: "InviteCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Rooms_OwnerId",
                table: "Rooms",
                column: "OwnerId");

            migrationBuilder.AddForeignKey(
                name: "FK_Channels_Rooms_RoomId",
                table: "Channels",
                column: "RoomId",
                principalTable: "Rooms",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Channels_Rooms_RoomId",
                table: "Channels");

            migrationBuilder.DropTable(
                name: "RoomMembers");

            migrationBuilder.DropTable(
                name: "Rooms");

            migrationBuilder.DropIndex(
                name: "IX_Channels_RoomId",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "RoomId",
                table: "Channels");
        }
    }
}
