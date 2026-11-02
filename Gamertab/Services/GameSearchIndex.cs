using System.Globalization;
using Gamertab.Models;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Analysis.TokenAttributes;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;

namespace Gamertab.Services;

public sealed class GameSearchIndex : IDisposable
{
    private readonly RAMDirectory directory = new();
    private readonly StandardAnalyzer analyzer = new(LuceneVersion.LUCENE_48);
    private readonly IndexWriter writer;
    private static readonly (string Name, float Weight)[] Fields = [("name", 5), ("genres", 2), ("themes", 2), ("summary", 1), ("modes", 1)];

    public GameSearchIndex() => writer = new IndexWriter(directory, new IndexWriterConfig(LuceneVersion.LUCENE_48, analyzer));

    public void Rebuild(IEnumerable<Game> games)
    {
        writer.DeleteAll();
        foreach (var game in games)
            Update(game);
    }

    public void Update(Game game)
    {
        var id = game.Id.ToString(CultureInfo.InvariantCulture);
        var document = new Document
        {
            new StringField("id", id, Field.Store.YES),
            new Int32Field("id_sort", game.Id, Field.Store.NO),
            new StringField("name_sort", game.Name.ToLowerInvariant(), Field.Store.NO),
            new TextField("name", game.Name, Field.Store.NO),
            new TextField("summary", game.Summary, Field.Store.NO),
            new TextField("genres", game.Genres, Field.Store.NO),
            new TextField("themes", game.Themes, Field.Store.NO),
            new TextField("modes", game.Modes, Field.Store.NO)
        };
        foreach (var genre in CatalogPage.Genres.Keys.Where(genre => game.Genres.Contains(genre, StringComparison.OrdinalIgnoreCase)))
            document.Add(new StringField("genre_filter", genre, Field.Store.NO));
        writer.UpdateDocument(new Term("id", id), document);
    }

    public GameSearchResults Search(string text, int skip, int limit, string? genre = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var terms = new List<string>();
        using (var tokens = analyzer.GetTokenStream("name", new StringReader(text)))
        {
            var term = tokens.AddAttribute<ICharTermAttribute>();
            tokens.Reset();
            while (tokens.IncrementToken())
                terms.Add(term.ToString());
            tokens.End();
        }
        if (terms.Count == 0)
            return new GameSearchResults([], 0);

        var query = new BooleanQuery();
        for (var position = 0; position < terms.Count; position++)
        {
            var alternatives = new BooleanQuery();
            foreach (var field in Fields)
            {
                alternatives.Add(new TermQuery(new Term(field.Name, terms[position])) { Boost = field.Weight }, Occur.SHOULD);
                if (position == terms.Count - 1 && terms[position].Length >= 2)
                    alternatives.Add(new PrefixQuery(new Term(field.Name, terms[position])) { Boost = field.Weight * 0.2f }, Occur.SHOULD);
            }
            query.Add(alternatives, Occur.MUST);
        }
        if (!string.IsNullOrEmpty(genre))
            query.Add(new TermQuery(new Term("genre_filter", genre)), Occur.MUST);

        using var reader = writer.GetReader(applyAllDeletes: true);
        if (reader.NumDocs == 0)
            return new GameSearchResults([], 0);
        var searcher = new IndexSearcher(reader);
        var sort = new Sort(SortField.FIELD_SCORE, new SortField("name_sort", SortFieldType.STRING), new SortField("id_sort", SortFieldType.INT32));
        var count = (int)Math.Min((long)skip + limit, reader.NumDocs);
        var matches = searcher.Search(query, count, sort);
        var ids = matches.ScoreDocs.Skip(skip).Take(limit).Select(hit => int.Parse(searcher.Doc(hit.Doc).Get("id"), CultureInfo.InvariantCulture)).ToArray();
        return new GameSearchResults(ids, matches.TotalHits);
    }

    public void Dispose()
    {
        writer.Dispose();
        analyzer.Dispose();
        directory.Dispose();
    }
}

public record GameSearchResults(int[] Ids, int Total);
