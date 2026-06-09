using Microsoft.Extensions.Options;

namespace TradeDemo.Api.Journal;

public static class TickJournalServiceCollectionExtensions
{
    public static IServiceCollection AddTickJournal(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TickJournalOptions>(configuration.GetSection("TickJournal"));

        services.AddSingleton<NullTickJournal>(_ => NullTickJournal.Instance);
        services.AddSingleton<LocalSegmentTickJournal>();
        services.AddSingleton<AzureBlobTickJournal>();
        services.AddSingleton<EventHubsTickJournal>();

        services.AddSingleton<ITickJournal>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<TickJournalOptions>>().Value;
            if (!options.Enabled || options.Provider.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                return sp.GetRequiredService<NullTickJournal>();
            }

            return options.Provider.ToUpperInvariant() switch
            {
                "LOCAL" => sp.GetRequiredService<LocalSegmentTickJournal>(),
                "BLOB" => sp.GetRequiredService<AzureBlobTickJournal>(),
                "EVENTHUBS" => sp.GetRequiredService<EventHubsTickJournal>(),
                _ => throw new InvalidOperationException($"Unknown TickJournal provider '{options.Provider}'.")
            };
        });

        services.AddSingleton<ITickJournalWriter>(sp => sp.GetRequiredService<ITickJournal>());
        services.AddSingleton<ITickJournalReader>(sp => sp.GetRequiredService<ITickJournal>());

        return services;
    }
}
