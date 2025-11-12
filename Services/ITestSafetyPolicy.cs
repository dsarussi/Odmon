using Odmon.Worker.Models;

namespace Odmon.Worker.Services
{
    public interface ITestSafetyPolicy
    {
        bool IsTestCase(OdcanitCase c);
    }
}

