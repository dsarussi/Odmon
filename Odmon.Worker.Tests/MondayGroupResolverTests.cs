using Microsoft.Extensions.Logging;
using Odmon.Worker.Monday;
using Xunit;

namespace Odmon.Worker.Tests
{
    public class MondayGroupResolverTests
    {
        private static ILogger CreateLogger() => new LoggerFactory().CreateLogger("MondayGroupResolverTests");

        [Fact]
        public void ResolveGroupId_ValidRequested_ReturnsRequested()
        {
            var boardGroupIds = new[] { "topics", "archived" };
            var result = MondayGroupResolver.ResolveGroupId(
                5035534500, boardGroupIds, "topics", "Monday:ToDoGroupId", CreateLogger());
            Assert.Equal("topics", result);
        }

        [Fact]
        public void ResolveGroupId_ValidRequested_SecondGroup_ReturnsRequested()
        {
            var boardGroupIds = new[] { "topics", "archived" };
            var result = MondayGroupResolver.ResolveGroupId(
                5035534500, boardGroupIds, "archived", "Monday:TestGroupId", CreateLogger());
            Assert.Equal("archived", result);
        }

        [Fact]
        public void ResolveGroupId_InvalidRequested_FallbackToFirst()
        {
            var boardGroupIds = new[] { "topics" };
            var result = MondayGroupResolver.ResolveGroupId(
                5035534500, boardGroupIds, "new_group29179", "Monday:ToDoGroupId", CreateLogger());
            Assert.Equal("topics", result);
        }

        [Fact]
        public void ResolveGroupId_NullRequested_FallbackToFirst()
        {
            var boardGroupIds = new[] { "topics", "other" };
            var result = MondayGroupResolver.ResolveGroupId(
                5035534500, boardGroupIds, null, "Monday:ToDoGroupId", CreateLogger());
            Assert.Equal("topics", result);
        }

        [Fact]
        public void ResolveGroupId_EmptyRequested_FallbackToFirst()
        {
            var boardGroupIds = new[] { "topics" };
            var result = MondayGroupResolver.ResolveGroupId(
                5035534500, boardGroupIds, "", "Monday:ToDoGroupId", CreateLogger());
            Assert.Equal("topics", result);
        }

        [Fact]
        public void ResolveGroupId_CaseSensitive_RequestedNotExactMatch_FallbackToFirst()
        {
            var boardGroupIds = new[] { "topics" };
            var result = MondayGroupResolver.ResolveGroupId(
                5035534500, boardGroupIds, "Topics", "Monday:ToDoGroupId", CreateLogger());
            Assert.Equal("topics", result);
        }

        [Fact]
        public void ResolveGroupId_ZeroGroups_Throws()
        {
            var boardGroupIds = Array.Empty<string>();
            var ex = Assert.Throws<InvalidOperationException>(() =>
                MondayGroupResolver.ResolveGroupId(
                    5035534500, boardGroupIds, "topics", "Monday:ToDoGroupId", CreateLogger()));
            Assert.Contains("no groups", ex.Message);
            Assert.Contains("5035534500", ex.Message);
        }

        [Fact]
        public void ResolveGroupId_NullList_Throws()
        {
            Assert.Throws<InvalidOperationException>(() =>
                MondayGroupResolver.ResolveGroupId(
                    5035534500, null!, "topics", "Monday:ToDoGroupId", CreateLogger()));
        }
    }
}
