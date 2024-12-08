#nullable enable
using System;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Rin.Core.Record;
using Rin.Extensions;
using Rin.Storage.Redis;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection
{
    public static class RinRedisRecordStorageServiceExtensions
    {
        /// <summary>
        /// Use the Redis-backed <see cref="IRecordStorage"/> service and options.
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configure"></param>
        public static IRinBuilder UseRedisStorage(this IRinBuilder builder, Action<RedisRecordStorageOptions>? configure = null)
        {
            builder.Services.AddOptions<RedisRecordStorageOptions>();

            if (configure != null) builder.Services.Configure<RedisRecordStorageOptions>(configure);

            builder.Services.Replace(new ServiceDescriptor(typeof(IRecordStorage), typeof(RedisRecordStorage), ServiceLifetime.Singleton));

            return builder;
        }
    }
}
