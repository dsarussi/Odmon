using System.Reflection;
using Microsoft.Data.SqlClient;

namespace Odmon.Worker.Tests
{
    public static class SqlExceptionTestHelper
    {
        public static SqlException Create(int number, string message)
        {
            var errorCtor = typeof(SqlError).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
                .OrderByDescending(c => c.GetParameters().Length)
                .First();
            var paramCount = errorCtor.GetParameters().Length;
            var args = new object?[paramCount];
            var paramInfos = errorCtor.GetParameters();
            for (int i = 0; i < paramCount; i++)
            {
                var p = paramInfos[i];
                args[i] = p.Name switch
                {
                    "infoNumber" => number,
                    "errorState" => (byte)1,
                    "errorClass" => (byte)17,
                    "server" => "test-server",
                    "errorMessage" or "message" => message,
                    "procedure" => "test-proc",
                    "lineNumber" => 1,
                    "win32ErrorCode" => (uint)0,
                    _ => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null
                };
            }
            var error = (SqlError)errorCtor.Invoke(args)!;
            var collection = (SqlErrorCollection)Activator.CreateInstance(
                typeof(SqlErrorCollection),
                BindingFlags.NonPublic | BindingFlags.Instance, null, null, null)!;
            typeof(SqlErrorCollection)
                .GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(collection, new object[] { error });
            var factory = typeof(SqlException).GetMethod(
                "CreateException",
                BindingFlags.NonPublic | BindingFlags.Static,
                null,
                new[] { typeof(SqlErrorCollection), typeof(string) },
                null);
            if (factory != null)
                return (SqlException)factory.Invoke(null, new object[] { collection, "16.0" })!;
            var ctor = typeof(SqlException).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).FirstOrDefault();
            if (ctor != null)
            {
                var ctorParams = ctor.GetParameters();
                var ctorArgs = new object?[ctorParams.Length];
                for (int i = 0; i < ctorParams.Length; i++)
                {
                    var p = ctorParams[i];
                    if (p.ParameterType == typeof(string)) ctorArgs[i] = message;
                    else if (p.ParameterType == typeof(SqlErrorCollection)) ctorArgs[i] = collection;
                    else if (p.ParameterType == typeof(Guid)) ctorArgs[i] = Guid.Empty;
                    else ctorArgs[i] = p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null;
                }
                return (SqlException)ctor.Invoke(ctorArgs)!;
            }
            throw new InvalidOperationException("Cannot create SqlException via reflection.");
        }
    }
}
