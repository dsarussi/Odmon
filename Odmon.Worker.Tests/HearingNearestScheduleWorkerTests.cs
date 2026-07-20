using Odmon.Worker.Workers;
using Xunit;

namespace Odmon.Worker.Tests
{
    public class HearingNearestScheduleWorkerTests
    {
        private static readonly TimeZoneInfo IsraelTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Israel Standard Time");

        [Fact]
        public void GetNextRunUtc_BeforeFirstRun_UsesSameDaySeven()
        {
            var nowUtc = ToUtc(2026, 7, 20, 6, 59);

            var next = HearingNearestScheduleWorker.GetNextRunUtc(nowUtc, DefaultTimes(), IsraelTimeZone);

            Assert.Equal(ToUtc(2026, 7, 20, 7, 0), next);
        }

        [Fact]
        public void GetNextRunUtc_BetweenRuns_UsesNextConfiguredTime()
        {
            var nowUtc = ToUtc(2026, 7, 20, 11, 1);

            var next = HearingNearestScheduleWorker.GetNextRunUtc(nowUtc, DefaultTimes(), IsraelTimeZone);

            Assert.Equal(ToUtc(2026, 7, 20, 15, 0), next);
        }

        [Fact]
        public void GetNextRunUtc_AfterLastRun_UsesNextDayFirstRun()
        {
            var nowUtc = ToUtc(2026, 7, 20, 23, 1);

            var next = HearingNearestScheduleWorker.GetNextRunUtc(nowUtc, DefaultTimes(), IsraelTimeZone);

            Assert.Equal(ToUtc(2026, 7, 21, 7, 0), next);
        }

        private static TimeOnly[] DefaultTimes()
        {
            return
            [
                new TimeOnly(7, 0),
                new TimeOnly(11, 0),
                new TimeOnly(15, 0),
                new TimeOnly(19, 0),
                new TimeOnly(23, 0)
            ];
        }

        private static DateTimeOffset ToUtc(int year, int month, int day, int hour, int minute)
        {
            var local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, IsraelTimeZone), TimeSpan.Zero);
        }
    }
}
