namespace DbUpX
{
    /// <summary>
    /// Controls which script content differences affect the SHA256 hash.
    /// </summary>
    public enum SqlScriptHashingMode
    {
        /// <summary>Hashes the original UTF-8 content, preserving legacy behavior.</summary>
        Raw = 0,

        /// <summary>Ignores a leading BOM and normalizes EOLs outside SQL Server quoted content.</summary>
        NormalizeSqlServer = 1,

        /// <summary>Ignores a leading BOM and normalizes EOLs outside PostgreSQL quoted content.</summary>
        NormalizePostgreSql = 2
    }
}
