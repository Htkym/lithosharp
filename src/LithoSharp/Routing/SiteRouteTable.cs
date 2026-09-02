using LithoSharp.Diagnostics;

namespace LithoSharp.Routing;

/// <summary>サイト全体のルートと生成成果物の所有権を収集し、一括して検証します。</summary>
public sealed class SiteRouteTable
{
    private readonly List<RouteRegistration> _routes = [];
    private readonly List<OutputReservation> _reservations = [];
    private readonly List<SiteDiagnostic> _diagnostics = [];

    /// <summary>空のルート表を作成します。</summary>
    public SiteRouteTable()
    {
    }

    /// <summary>正規化済みサイトルートを所有者とともに登録します。</summary>
    /// <param name="route">登録するサイトルート。</param>
    /// <param name="ownerId">ルートを生成する要素の安定した識別子。</param>
    /// <param name="sourceLocation">ルートの定義元。特定できない場合は <see langword="null"/>。</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="route"/> または <paramref name="ownerId"/> が <see langword="null"/> です。
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="ownerId"/> が空白です。</exception>
    public void Register(
        SiteRoute route,
        string ownerId,
        SiteSourceLocation? sourceLocation = null)
    {
        AddRoute(route, ownerId, sourceLocation, claimsOutput: true);
    }

    internal void RegisterPublicRoute(
        SiteRoute route,
        string ownerId,
        SiteSourceLocation? sourceLocation = null)
    {
        AddRoute(route, ownerId, sourceLocation, claimsOutput: false);
    }

    internal void RegisterInvalidRoute(
        string path,
        string ownerId,
        Exception failure,
        SiteSourceLocation? sourceLocation = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(failure);
        ValidateOwnerId(ownerId);
        _reservations.Add(new OutputReservation(
            path,
            NormalizedPath: null,
            ownerId,
            sourceLocation,
            $"{failure.GetType().Name}: {failure.Message}"));
    }

    internal void RegisterDiagnostic(SiteDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        _diagnostics.Add(diagnostic);
    }

    /// <summary>ジェネレーターまたはテンプレートが所有する共通成果物の出力パスを予約します。</summary>
    /// <param name="relativeOutputPath"><c>/</c> 区切りの出力ルートからの相対ファイルパス。</param>
    /// <param name="ownerId">成果物を生成する要素の安定した識別子。</param>
    /// <param name="sourceLocation">予約の定義元。特定できない場合は <see langword="null"/>。</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="relativeOutputPath"/> または <paramref name="ownerId"/> が <see langword="null"/> です。
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="ownerId"/> が空白です。</exception>
    /// <remarks>
    /// 予約対象は呼び出し側が指定します。この表は特定のテンプレートや成果物名を組み込みません。
    /// 不正な出力パスは登録時に外部へスローせず、<see cref="Validate"/> の診断として報告します。
    /// </remarks>
    public void ReserveOutputPath(
        string relativeOutputPath,
        string ownerId,
        SiteSourceLocation? sourceLocation = null)
    {
        ArgumentNullException.ThrowIfNull(relativeOutputPath);
        ValidateOwnerId(ownerId);

        try
        {
            var normalizedPath = SiteRoute.NormalizeRelativeOutputPath(relativeOutputPath);
            _reservations.Add(new OutputReservation(
                relativeOutputPath,
                normalizedPath,
                ownerId,
                sourceLocation,
                ErrorMessage: null));
        }
        catch (Exception exception) when (exception is ArgumentException or UriFormatException)
        {
            _reservations.Add(new OutputReservation(
                relativeOutputPath,
                NormalizedPath: null,
                ownerId,
                sourceLocation,
                exception.Message));
        }
    }

    /// <summary>登録済みのすべてのルートと予約を検証し、すべての診断を返します。</summary>
    /// <returns>安定した順序に並べられた診断を含む検証結果。</returns>
    public SiteRouteValidationResult Validate()
    {
        var diagnostics = new List<SiteDiagnostic>(_diagnostics);
        var routes = _routes
            .OrderBy(registration => registration.Route.PublicPath, StringComparer.Ordinal)
            .ThenBy(registration => registration.Route.RelativeOutputPath, StringComparer.Ordinal)
            .ThenBy(registration => registration.OwnerId, StringComparer.Ordinal)
            .ThenBy(registration => registration.SourceLocation?.FilePath, StringComparer.Ordinal)
            .ThenBy(registration => registration.SourceLocation?.Line)
            .ThenBy(registration => registration.SourceLocation?.Column)
            .GroupBy(registration => new RouteClaimIdentity(
                registration.Route.PublicPath,
                registration.Route.RelativeOutputPath,
                registration.OwnerId))
            .Select(group =>
            {
                var first = group.First();
                return first with
                {
                    SourceLocation = group
                        .Select(registration => registration.SourceLocation)
                        .FirstOrDefault(location => location is not null),
                    ClaimsOutput = group.Any(registration => registration.ClaimsOutput),
                };
            })
            .ToArray();
        var reservations = _reservations
            .OrderBy(reservation => reservation.NormalizedPath ?? reservation.OriginalPath, StringComparer.Ordinal)
            .ThenBy(reservation => reservation.OwnerId, StringComparer.Ordinal)
            .ThenBy(reservation => reservation.SourceLocation?.FilePath, StringComparer.Ordinal)
            .ThenBy(reservation => reservation.SourceLocation?.Line)
            .ThenBy(reservation => reservation.SourceLocation?.Column)
            .DistinctBy(reservation => new ReservationClaimIdentity(
                reservation.NormalizedPath,
                reservation.NormalizedPath is null ? reservation.OriginalPath : null,
                reservation.OwnerId))
            .ToArray();

        AddInvalidRouteDiagnostics(routes, reservations, diagnostics);
        AddRouteCollisionDiagnostics(routes, diagnostics);
        AddReservationCollisionDiagnostics(routes, reservations, diagnostics);
        AddOutputPathAncestorConflictDiagnostics(routes, reservations, diagnostics);

        var orderedDiagnostics = diagnostics
            .OrderBy(diagnostic => diagnostic.Id, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Message, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Location?.FilePath, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Location?.Line)
            .ThenBy(diagnostic => diagnostic.Location?.Column)
            .ToArray();
        return new SiteRouteValidationResult(orderedDiagnostics);
    }

    /// <summary>登録済みのすべてのルートと予約を検証し、エラーがあれば例外をスローします。</summary>
    /// <returns>エラーのない検証結果。</returns>
    /// <exception cref="SiteRouteValidationException">1 件以上のエラー診断が見つかりました。</exception>
    public SiteRouteValidationResult ValidateOrThrow()
    {
        var result = Validate();
        if (!result.IsValid)
        {
            throw new SiteRouteValidationException(result.Diagnostics);
        }

        return result;
    }

    private static void AddInvalidRouteDiagnostics(
        IReadOnlyList<RouteRegistration> routes,
        IReadOnlyList<OutputReservation> reservations,
        ICollection<SiteDiagnostic> diagnostics)
    {
        foreach (var registration in routes)
        {
            try
            {
                var normalized = SiteRoute.NormalizeRelativeOutputPath(
                    registration.Route.RelativeOutputPath);
                if (!StringComparer.Ordinal.Equals(normalized, registration.Route.RelativeOutputPath)
                    || !IsSafePublicPath(registration.Route.PublicPath))
                {
                    AddInvalidRouteDiagnostic(
                        registration.Route.RelativeOutputPath,
                        registration.OwnerId,
                        registration.SourceLocation,
                        "The route is not canonical.");
                }
            }
            catch (Exception exception) when (exception is ArgumentException or UriFormatException)
            {
                AddInvalidRouteDiagnostic(
                    registration.Route.RelativeOutputPath,
                    registration.OwnerId,
                    registration.SourceLocation,
                    exception.Message);
            }
        }

        foreach (var reservation in reservations)
        {
            if (reservation.ErrorMessage is not null)
            {
                AddInvalidRouteDiagnostic(
                    reservation.OriginalPath,
                    reservation.OwnerId,
                    reservation.SourceLocation,
                    reservation.ErrorMessage);
            }
        }

        void AddInvalidRouteDiagnostic(
            string path,
            string ownerId,
            SiteSourceLocation? location,
            string reason) =>
            diagnostics.Add(new SiteDiagnostic(
                SiteRouteDiagnosticIds.InvalidRoute,
                SiteDiagnosticSeverity.Error,
                $"所有者 '{ownerId}' の出力パス '{path}' は無効です: {reason}",
                location));
    }

    private static void AddRouteCollisionDiagnostics(
        IReadOnlyList<RouteRegistration> routes,
        ICollection<SiteDiagnostic> diagnostics)
    {
        foreach (var group in routes.GroupBy(
                     route => route.Route.PublicPath,
                     StringComparer.Ordinal))
        {
            AddPairDiagnostics(group, (left, right) =>
            {
                var location = right.SourceLocation ?? left.SourceLocation;
                diagnostics.Add(Collision(
                    SiteRouteDiagnosticIds.DuplicatePublicPath,
                    $"公開パス '{left.Route.PublicPath}' は所有者 '{left.OwnerId}' と '{right.OwnerId}' で重複しています。",
                    location));
            });
        }

        var outputRoutes = routes.Where(route => route.ClaimsOutput).ToArray();
        foreach (var group in outputRoutes.GroupBy(
                     route => route.Route.RelativeOutputPath,
                     StringComparer.Ordinal))
        {
            AddPairDiagnostics(group, (left, right) =>
            {
                diagnostics.Add(Collision(
                    SiteRouteDiagnosticIds.DuplicateOutputPath,
                    $"出力パス '{left.Route.RelativeOutputPath}' は所有者 '{left.OwnerId}' と '{right.OwnerId}' で重複しています。",
                    right.SourceLocation ?? left.SourceLocation));
            });
        }

        foreach (var group in outputRoutes.GroupBy(
                     route => route.Route.RelativeOutputPath,
                     StringComparer.OrdinalIgnoreCase))
        {
            AddPairDiagnostics(group, (left, right) =>
            {
                if (!StringComparer.Ordinal.Equals(
                        left.Route.RelativeOutputPath,
                        right.Route.RelativeOutputPath))
                {
                    diagnostics.Add(Collision(
                        SiteRouteDiagnosticIds.OutputPathCaseCollision,
                        $"出力パス '{left.Route.RelativeOutputPath}' と '{right.Route.RelativeOutputPath}' は大文字と小文字だけが異なります。",
                        right.SourceLocation ?? left.SourceLocation));
                }
            });
        }
    }

    private static void AddReservationCollisionDiagnostics(
        IReadOnlyList<RouteRegistration> routes,
        IReadOnlyList<OutputReservation> reservations,
        ICollection<SiteDiagnostic> diagnostics)
    {
        var validReservations = reservations
            .Where(reservation => reservation.NormalizedPath is not null)
            .ToArray();
        var outputRoutesByPath = routes
            .Where(route => route.ClaimsOutput)
            .GroupBy(
                route => route.Route.RelativeOutputPath,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        foreach (var reservation in validReservations)
        {
            if (!outputRoutesByPath.TryGetValue(reservation.NormalizedPath!, out var matchingRoutes))
            {
                continue;
            }

            foreach (var route in matchingRoutes)
            {
                if (!StringComparer.Ordinal.Equals(reservation.OwnerId, route.OwnerId))
                {
                    diagnostics.Add(Collision(
                        SiteRouteDiagnosticIds.ReservedOutputPathCollision,
                        $"予約済み出力パス '{reservation.NormalizedPath}' の所有者 '{reservation.OwnerId}' とルート所有者 '{route.OwnerId}' が競合しています。",
                        route.SourceLocation ?? reservation.SourceLocation));
                }
            }
        }

        foreach (var group in validReservations.GroupBy(
                     reservation => reservation.NormalizedPath!,
                     StringComparer.OrdinalIgnoreCase))
        {
            AddPairDiagnostics(group, (reservation, other) =>
            {
                if (!StringComparer.Ordinal.Equals(reservation.OwnerId, other.OwnerId)
                    && reservation.NormalizedPath is not null)
                {
                    diagnostics.Add(Collision(
                        SiteRouteDiagnosticIds.ReservedOutputPathCollision,
                        $"出力パス '{reservation.NormalizedPath}' は所有者 '{reservation.OwnerId}' と '{other.OwnerId}' に予約されています。",
                        other.SourceLocation ?? reservation.SourceLocation));
                }
            });
        }
    }

    private static void AddOutputPathAncestorConflictDiagnostics(
        IReadOnlyList<RouteRegistration> routes,
        IReadOnlyList<OutputReservation> reservations,
        ICollection<SiteDiagnostic> diagnostics)
    {
        var claims = routes
            .Where(route => route.ClaimsOutput)
            .Select(route => new OutputClaim(
                route.Route.RelativeOutputPath,
                route.OwnerId,
                route.SourceLocation))
            .Concat(reservations
                .Where(reservation => reservation.NormalizedPath is not null)
                .Select(reservation => new OutputClaim(
                    reservation.NormalizedPath!,
                    reservation.OwnerId,
                    reservation.SourceLocation)))
            .OrderBy(claim => claim.Path, StringComparer.Ordinal)
            .ThenBy(claim => claim.OwnerId, StringComparer.Ordinal)
            .ThenBy(claim => claim.SourceLocation?.FilePath, StringComparer.Ordinal)
            .ThenBy(claim => claim.SourceLocation?.Line)
            .ThenBy(claim => claim.SourceLocation?.Column)
            .DistinctBy(claim => new OutputClaimIdentity(claim.Path, claim.OwnerId))
            .ToArray();
        var claimsByPath = claims
            .GroupBy(claim => claim.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        foreach (var descendant in claims)
        {
            foreach (var ancestorPath in EnumerateAncestorPaths(descendant.Path))
            {
                if (!claimsByPath.TryGetValue(ancestorPath, out var ancestors))
                {
                    continue;
                }

                foreach (var ancestor in ancestors)
                {
                    diagnostics.Add(Collision(
                        SiteRouteDiagnosticIds.OutputPathAncestorConflict,
                        $"出力パス '{ancestor.Path}' の所有者 '{ancestor.OwnerId}' と子出力パス '{descendant.Path}' の所有者 '{descendant.OwnerId}' は、ファイルとディレクトリとして共存できません。",
                        descendant.SourceLocation ?? ancestor.SourceLocation));
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateAncestorPaths(string path)
    {
        for (var index = path.IndexOf('/'); index >= 0; index = path.IndexOf('/', index + 1))
        {
            yield return path[..index];
        }
    }

    private static void AddPairDiagnostics<T>(
        IEnumerable<T> values,
        Action<T, T> addDiagnostic)
    {
        var items = values.ToArray();
        for (var leftIndex = 0; leftIndex < items.Length; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1; rightIndex < items.Length; rightIndex++)
            {
                addDiagnostic(items[leftIndex], items[rightIndex]);
            }
        }
    }

    private static SiteDiagnostic Collision(
        string id,
        string message,
        SiteSourceLocation? location) =>
        new(id, SiteDiagnosticSeverity.Error, message, location);

    private static bool IsSafePublicPath(string publicPath) =>
        publicPath.Length > 0
        && publicPath[0] == '/'
        && !publicPath.Contains('\\')
        && publicPath.IndexOfAny(['?', '#']) < 0
        && !publicPath.Any(char.IsControl);

    private static void ValidateOwnerId(string ownerId)
    {
        ArgumentNullException.ThrowIfNull(ownerId);
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            throw new ArgumentException("A route owner identifier must not be empty.", nameof(ownerId));
        }
    }

    private void AddRoute(
        SiteRoute route,
        string ownerId,
        SiteSourceLocation? sourceLocation,
        bool claimsOutput)
    {
        ArgumentNullException.ThrowIfNull(route);
        ValidateOwnerId(ownerId);
        _routes.Add(new RouteRegistration(route, ownerId, sourceLocation, claimsOutput));
    }

    private sealed record RouteRegistration(
        SiteRoute Route,
        string OwnerId,
        SiteSourceLocation? SourceLocation,
        bool ClaimsOutput);

    private sealed record OutputReservation(
        string OriginalPath,
        string? NormalizedPath,
        string OwnerId,
        SiteSourceLocation? SourceLocation,
        string? ErrorMessage);

    private sealed record OutputClaim(
        string Path,
        string OwnerId,
        SiteSourceLocation? SourceLocation);

    private readonly record struct RouteClaimIdentity(
        string PublicPath,
        string RelativeOutputPath,
        string OwnerId);

    private readonly record struct ReservationClaimIdentity(
        string? NormalizedPath,
        string? InvalidOriginalPath,
        string OwnerId);

    private readonly record struct OutputClaimIdentity(string Path, string OwnerId);
}
