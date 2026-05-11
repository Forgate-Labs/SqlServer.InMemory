namespace SqlServer.InMemory.Exceptions;

public class SqlServerInMemoryException : Exception
{
    public SqlServerInMemoryException()
    {
    }

    public SqlServerInMemoryException(string message)
        : base(message)
    {
    }

    public SqlServerInMemoryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public class SqlServerInMemoryConstraintException : SqlServerInMemoryException
{
    public SqlServerInMemoryConstraintException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public class SqlServerInMemoryUniqueConstraintException : SqlServerInMemoryConstraintException
{
    public SqlServerInMemoryUniqueConstraintException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public class SqlServerInMemoryForeignKeyException : SqlServerInMemoryConstraintException
{
    public SqlServerInMemoryForeignKeyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
