using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Discagem.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChannelKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "Channels",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // Every server starts with one voice channel.
            migrationBuilder.Sql(
                "INSERT INTO Channels (Name, Topic, Position, CreatedAt, Kind) " +
                "VALUES ('Lounge', 'Hang out and share your screen', 1000, strftime('%Y-%m-%d %H:%M:%f', 'now'), 1);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM Channels WHERE Kind = 1;");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Channels");
        }
    }
}
