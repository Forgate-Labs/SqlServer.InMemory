using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;

namespace SqlServer.InMemory.EFCore.Conventions;

internal sealed class SqlServerInMemoryConventionSetPlugin : IConventionSetPlugin
{
    private readonly SqlServerInMemoryOptions _options;

    public SqlServerInMemoryConventionSetPlugin(SqlServerInMemoryOptions options)
    {
        _options = options;
    }

    public ConventionSet ModifyConventions(ConventionSet conventionSet)
    {
        conventionSet.ModelFinalizingConventions.Add(new SqlServerInMemoryModelFinalizingConvention(_options));
        return conventionSet;
    }
}
