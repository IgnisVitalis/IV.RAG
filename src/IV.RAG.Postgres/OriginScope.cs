using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace IV.RAG;

// Builds the optional origin-scope WHERE predicates (access-control filter) shared by the vector and
// lexical retrievers. Values are passed as parameters, never interpolated. The predicates hit the
// existing {table}_origin_idx (source_id, document_type, document_id).
internal static class OriginScope
{
    internal static string BuildClause(RetrievalOptions options, NpgsqlCommand command)
    {
        var sb = new StringBuilder();

        if (options.SourceId is { } sourceId)
        {
            sb.Append("\n          AND source_id = @originSourceId");
            command.Parameters.Add(new NpgsqlParameter("originSourceId", NpgsqlDbType.Uuid) { Value = sourceId });
        }

        if (options.DocumentType is { } documentType)
        {
            sb.Append("\n          AND document_type = @originDocumentType");
            command.Parameters.AddWithValue("originDocumentType", documentType);
        }

        if (options.DocumentId is { } documentId)
        {
            sb.Append("\n          AND document_id = @originDocumentId");
            command.Parameters.AddWithValue("originDocumentId", documentId);
        }

        return sb.ToString();
    }
}
