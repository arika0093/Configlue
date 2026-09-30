using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Configlue.Extensions.MSOptions;

/// <summary>Bridges Microsoft options validators into Configlue state validation.</summary>
public static class ConfiglueMicrosoftOptionsValidationExtensions
{
    /// <summary>Registers a standard Microsoft options validator for state writes.</summary>
    public static IServiceCollection AddConfiglueValidator<TModel>(
        this IServiceCollection services,
        IValidateOptions<TModel> validator
    )
        where TModel : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(validator);
        services.AddSingleton<IConfiglueValidator<TModel>>(
            new ValidateOptionsAdapter<TModel>(validator)
        );
        return services;
    }

    private sealed class ValidateOptionsAdapter<TModel>(IValidateOptions<TModel> validator)
        : INamedConfiglueValidator<TModel>
        where TModel : class
    {
        public IReadOnlyList<string> Validate(TModel value) => Validate(Options.DefaultName, value);

        public IReadOnlyList<string> Validate(string? name, TModel value)
        {
            var result = validator.Validate(name ?? Options.DefaultName, value);
            return result.Failed ? result.Failures?.ToArray() ?? [] : [];
        }
    }
}
