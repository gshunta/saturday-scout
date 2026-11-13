import { useEffect, useMemo, useRef, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { ApiError, getActiveSlate, getMyPickSheet, saveMyPickSheet, submitMyPickSheet } from '../api/api';
import type { PickDto, PickSheetDto, PlayerSlateDto } from '../api/contracts';
import { EmptyState, ErrorMessage, Loading } from '../components/States';
import { useLeagueSelection } from '../leagues/LeagueSelection';
import { formatLocalDateTime } from '../utils/dateTime';

interface DraftRow { teamId: string; rank: string }
type Draft = Record<string, DraftRow>;
const blankDraft = (): Draft => ({});

function picksFromDraft(slate: PlayerSlateDto, draft: Draft): PickDto[] {
  return slate.games.flatMap(game => {
    const row = draft[game.gameId];
    return row?.teamId && row.rank ? [{ gameId: game.gameId, selectedTeamId: row.teamId, confidenceRank: Number(row.rank) }] : [];
  });
}

function draftFromSheet(slate: PlayerSlateDto, sheet: PickSheetDto | null): Draft {
  const result: Draft = {};
  for (const pick of sheet?.picks ?? []) {
    if (slate.games.some(game => game.gameId === pick.gameId)) {
      result[pick.gameId] = { teamId: pick.selectedTeamId, rank: String(pick.confidenceRank) };
    }
  }
  return result;
}

function sameDraft(a: Draft, b: Draft, slate: PlayerSlateDto): boolean {
  return slate.games.every(game => (a[game.gameId]?.teamId ?? '') === (b[game.gameId]?.teamId ?? '') &&
    (a[game.gameId]?.rank ?? '') === (b[game.gameId]?.rank ?? ''));
}

function validation(slate: PlayerSlateDto, draft: Draft) {
  const incomplete = slate.games.filter(game => {
    const row = draft[game.gameId];
    return Boolean(row?.teamId) !== Boolean(row?.rank);
  });
  const missingWinners = slate.games.filter(game => !draft[game.gameId]?.teamId);
  const ranks = slate.games.map(game => draft[game.gameId]?.rank).filter(Boolean) as string[];
  const duplicateRanks = [...new Set(ranks.filter((rank, index) => ranks.indexOf(rank) !== index))];
  const invalidRanks = ranks.filter(rank => !/^(10|[1-9])$/.test(rank));
  const missingRanks = Array.from({ length: 10 }, (_, index) => String(index + 1)).filter(rank => !ranks.includes(rank));
  return { incomplete, missingWinners, duplicateRanks, invalidRanks, missingRanks,
    ready: slate.games.length === 10 && missingWinners.length === 0 && incomplete.length === 0 &&
      duplicateRanks.length === 0 && invalidRanks.length === 0 && missingRanks.length === 0 };
}

function isLocked(slate: PlayerSlateDto | null, now: number): boolean {
  if (!slate || slate.status !== 'Published' || !slate.lockUtc) return true;
  const lock = Date.parse(slate.lockUtc);
  return !Number.isFinite(lock) || lock <= now;
}

function errorText(error: unknown): string {
  if (error instanceof ApiError) return error.message;
  return 'Saturday Scout could not complete that request. Your current selections are still on this page.';
}

export function PicksPage() {
  const { leagues, loading: leaguesLoading, error: leaguesError, selectedLeague, retryLeagues } = useLeagueSelection();
  const queryClient = useQueryClient();
  const slateQuery = useQuery({
    queryKey: ['active-player-slate', selectedLeague?.id],
    queryFn: () => getActiveSlate(selectedLeague!.id),
    enabled: Boolean(selectedLeague), retry: false,
  });
  const slate = slateQuery.data ?? null;
  const sheetQuery = useQuery({
    queryKey: ['player-pick-sheet', slate?.id],
    queryFn: () => getMyPickSheet(slate!.id),
    enabled: Boolean(slate), retry: false,
  });
  const sheet = sheetQuery.data ?? null;
  const [draft, setDraft] = useState<Draft>(blankDraft);
  const [initializedId, setInitializedId] = useState<string | null>(null);
  const [now, setNow] = useState(Date.now());
  const [reviewOpen, setReviewOpen] = useState(false);
  const [saving, setSaving] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [message, setMessage] = useState('');
  const [error, setError] = useState('');
  const [conflict, setConflict] = useState(false);
  const busyRef = useRef(false);

  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 15_000);
    return () => window.clearInterval(timer);
  }, []);

  useEffect(() => {
    if (slate && sheetQuery.isSuccess && initializedId !== slate.id) {
      setDraft(draftFromSheet(slate, sheet));
      setInitializedId(slate.id);
      setConflict(false);
    }
  }, [slate, sheet, sheetQuery.isSuccess, initializedId]);

  const isDirty = Boolean(slate && initializedId === slate.id && !sameDraft(draft, draftFromSheet(slate, sheet), slate));
  const checks = useMemo(() => slate ? validation(slate, draft) : null, [slate, draft]);
  const locked = isLocked(slate, now);
  const submitted = sheet?.status === 'Submitted';
  const readOnly = locked || submitted || conflict || !slate || initializedId !== slate.id;
  const saveIssues = checks && (checks.incomplete.length > 0 || checks.duplicateRanks.length > 0 || checks.invalidRanks.length > 0);

  const applyServerSheet = (next: PickSheetDto | null) => {
    if (!slate) return;
    queryClient.setQueryData(['player-pick-sheet', slate.id], next);
    setDraft(draftFromSheet(slate, next));
    setInitializedId(slate.id);
    setConflict(false);
  };

  const handleSave = async () => {
    if (!slate || readOnly || !isDirty || saveIssues || busyRef.current) return;
    busyRef.current = true; setSaving(true); setError(''); setMessage('Saving draft…');
    try {
      const saved = await saveMyPickSheet(slate.id, picksFromDraft(slate, draft), sheet?.version ?? 0);
      applyServerSheet(saved);
      setMessage('Draft saved. Your selections are available when you return.');
    } catch (cause) {
      setError(errorText(cause)); setMessage('');
      if (cause instanceof ApiError && (cause.status === 409 || cause.status === null || cause.status >= 500)) {
        if (cause.status === 409) {
          await queryClient.invalidateQueries({ queryKey: ['active-player-slate', slate.leagueId] });
        }
        try {
          const latest = await getMyPickSheet(slate.id);
          queryClient.setQueryData(['player-pick-sheet', slate.id], latest);
          if (sameDraft(draft, draftFromSheet(slate, latest), slate)) {
            applyServerSheet(latest); setMessage('The server confirms this draft is saved.'); setError('');
          } else {
            setConflict(true);
            setError(cause.status === 409
              ? 'The server draft changed or the slate locked. Your local selections are preserved. Load the server version before continuing.'
              : 'The save response was interrupted. Your local selections are preserved; compare with the server version before trying again.');
          }
        } catch { setError('The save result is uncertain. Your selections remain on this page. Check the server version before trying again.'); }
      }
    } finally { busyRef.current = false; setSaving(false); }
  };

  const handleSubmit = async () => {
    if (!slate || !sheet || sheet.status !== 'Draft' || !checks?.ready || isDirty || readOnly || busyRef.current) return;
    busyRef.current = true; setSubmitting(true); setError(''); setMessage('Submitting…');
    try {
      const result = await submitMyPickSheet(slate.id, sheet.version);
      applyServerSheet(result); setMessage('Your picks are submitted and now read-only.'); setReviewOpen(false);
    } catch (cause) {
      setMessage(''); setError(errorText(cause));
      if (cause instanceof ApiError && (cause.status === 409 || cause.status === null || cause.status >= 500)) {
        if (cause.status === 409) {
          await queryClient.invalidateQueries({ queryKey: ['active-player-slate', slate.leagueId] });
        }
        try {
          const latest = await getMyPickSheet(slate.id);
          queryClient.setQueryData(['player-pick-sheet', slate.id], latest);
          if (latest?.status === 'Submitted') {
            applyServerSheet(latest); setMessage('Your picks were submitted. The server confirms this sheet is read-only.'); setError(''); setReviewOpen(false);
          } else setConflict(true);
        } catch { setError('The submission result is uncertain. Check the server sheet before attempting another submission.'); }
      }
    } finally { busyRef.current = false; setSubmitting(false); }
  };

  if (leaguesLoading) return <Loading label="Loading your leagues…" />;
  if (leaguesError) return <ErrorMessage message={leaguesError.message} onRetry={() => void retryLeagues()} />;
  if (!leagues?.length) return <EmptyState title="No league memberships"><p>Join your invited league to make picks.</p></EmptyState>;
  if (slateQuery.isPending) return <Loading label="Loading the published slate…" />;
  if (slateQuery.error) return <ErrorMessage message={slateQuery.error.message} onRetry={() => void slateQuery.refetch()} />;
  if (!slate) return <EmptyState title="No published slate available"><p>There is no active published slate for {selectedLeague?.name} right now.</p></EmptyState>;
  if (sheetQuery.isPending) return <Loading label="Loading your saved picks…" />;
  if (sheetQuery.error) return <ErrorMessage message={sheetQuery.error.message} onRetry={() => void sheetQuery.refetch()} />;

  return (
    <section className="page-stack picks-page" aria-labelledby="picks-title">
      <header className="page-heading">
        <p className="eyebrow">{selectedLeague?.name} · College football</p>
        <h1 id="picks-title">Week {slate.week} picks</h1>
        <p className="muted">{slate.season} season · Lock: {formatLocalDateTime(slate.lockUtc) ?? 'time unavailable'}</p>
      </header>

      <div className="picks-status-row">
        <span className={`status-pill ${submitted ? 'picks-submitted' : locked ? 'picks-locked' : ''}`}>
          {submitted ? 'Submitted' : locked ? (slate.status === 'Published' ? 'Locked' : slate.status) : 'Open'}
        </span>
        <span className="muted" role="status">{submitted ? 'Your submitted picks are read-only.' : locked ? 'This slate no longer accepts changes.' : `${slate.games.length} games`}</span>
      </div>
      {error && <div className="notice notice-error" role="alert">{error}{conflict && <button className="button button-secondary picks-resolve" type="button" onClick={() => { setDraft(draftFromSheet(slate, sheet)); setConflict(false); setMessage('Loaded the latest server draft.'); setError(''); }}>Load server version</button>}</div>}
      {message && <p className="notice picks-message" role="status">{message}</p>}

      {slate.games.length === 0 ? <EmptyState title="No games in this slate"><p>There are no games to pick right now.</p></EmptyState> : <div className="picks-game-list">
        {slate.games.slice().sort((a, b) => a.displayOrder - b.displayOrder).map(game => {
          const row = draft[game.gameId] ?? { teamId: '', rank: '' };
          const ranksUsedElsewhere = new Set(slate.games.filter(other => other.gameId !== game.gameId).map(other => draft[other.gameId]?.rank).filter(Boolean));
          return <article className="picks-game-card" key={game.gameId}>
            <div className="picks-game-meta"><span>Game {game.displayOrder}</span><span>{game.status}</span></div>
            <p className="picks-kickoff">{formatLocalDateTime(game.kickoffUtc) ?? 'Kickoff time unavailable'}</p>
            <h2 className="picks-matchup">{game.awayTeam} <span aria-hidden="true">@</span> {game.homeTeam}</h2>
            {game.status === 'Final' && game.awayScore !== null && game.homeScore !== null && <p className="picks-score">Final · {game.awayScore}–{game.homeScore}</p>}
            <fieldset className="picks-winner-fieldset" disabled={readOnly}>
              <legend>Choose the winner</legend>
              <div className="picks-teams">
                <label className={`picks-team ${row.teamId === game.awayTeamId ? 'selected' : ''}`}>
                  <input type="radio" name={`winner-${game.gameId}`} value={game.awayTeamId} checked={row.teamId === game.awayTeamId}
                    onChange={() => { setDraft(current => ({ ...current, [game.gameId]: { ...row, teamId: game.awayTeamId } })); setError(''); setMessage('Unsaved changes'); }} />
                  <span>{game.awayTeam} <small>{game.awayAbbreviation}</small></span>{row.teamId === game.awayTeamId && <b aria-label="Selected winner">Selected</b>}
                </label>
                <label className={`picks-team ${row.teamId === game.homeTeamId ? 'selected' : ''}`}>
                  <input type="radio" name={`winner-${game.gameId}`} value={game.homeTeamId} checked={row.teamId === game.homeTeamId}
                    onChange={() => { setDraft(current => ({ ...current, [game.gameId]: { ...row, teamId: game.homeTeamId } })); setError(''); setMessage('Unsaved changes'); }} />
                  <span>{game.homeTeam} <small>{game.homeAbbreviation}</small></span>{row.teamId === game.homeTeamId && <b aria-label="Selected winner">Selected</b>}
                </label>
              </div>
            </fieldset>
            <label className="picks-confidence">Confidence rank
              <select aria-label={`Confidence rank for ${game.awayTeam} at ${game.homeTeam}`} value={row.rank} disabled={readOnly}
                onChange={event => { setDraft(current => ({ ...current, [game.gameId]: { ...row, rank: event.target.value } })); setError(''); setMessage('Unsaved changes'); }}>
                <option value="">Choose 1–10</option>
                {Array.from({ length: 10 }, (_, i) => String(i + 1)).map(rank => <option key={rank} value={rank} disabled={ranksUsedElsewhere.has(rank)}>{rank}{ranksUsedElsewhere.has(rank) ? ' · used' : ''}</option>)}
              </select>
            </label>
            {!readOnly && (row.teamId || row.rank) && <button className="picks-clear" type="button" onClick={() => { setDraft(current => { const next = { ...current }; delete next[game.gameId]; return next; }); setError(''); setMessage('Unsaved changes'); }}>Clear this game</button>}
          </article>;
        })}
      </div>}

      {checks && <section className="picks-validation" aria-live="polite" aria-label="Pick validation">
        {checks.incomplete.length > 0 && <p>Complete or clear both the winner and rank on {checks.incomplete.length} partially filled game(s) before saving.</p>}
        {checks.duplicateRanks.length > 0 && <p>Confidence ranks used more than once: {checks.duplicateRanks.join(', ')}.</p>}
        {checks.invalidRanks.length > 0 && <p>Confidence ranks must be whole numbers from 1 through 10.</p>}
        {checks.ready && <p>All {slate.games.length} games have unique confidence ranks 1–10.</p>}
        {!checks.ready && checks.missingWinners.length > 0 && <p>{checks.missingWinners.length} game(s) still need a winner.</p>}
        {sheet?.status === 'Draft' && checks.missingRanks.length > 0 && <p>Ranks still available: {checks.missingRanks.join(', ')}.</p>}
      </section>}

      {submitted ? <div className="notice picks-submitted-notice" role="status">Submitted{formatLocalDateTime(sheet.submittedUtc) ? ` ${formatLocalDateTime(sheet.submittedUtc)}` : ''}. Your picks are final and read-only.</div>
        : locked ? <div className="notice picks-locked-notice" role="status">This slate is locked. Existing picks remain visible, but changes and submissions are disabled.</div>
        : <div className="picks-actions">
          <span className="muted" role="status">{saving ? 'Saving…' : isDirty ? 'Unsaved changes' : sheet ? 'Draft saved' : 'No draft saved'}</span>
          <button className="button button-secondary" type="button" disabled={!isDirty || Boolean(saveIssues) || saving || submitting || conflict} onClick={() => void handleSave()}>{saving ? 'Saving…' : 'Save draft'}</button>
          <button className="button button-primary" type="button" disabled={saving || submitting || conflict} onClick={() => setReviewOpen(true)}>Review submission</button>
        </div>}

      {reviewOpen && <div className="picks-review-backdrop">
        <section className="picks-review" role="dialog" aria-modal="true" aria-labelledby="picks-review-title">
          <p className="eyebrow">Final review</p><h2 id="picks-review-title">Review your picks</h2>
          <p>Submission is final. Verify every winner and confidence rank before continuing.</p>
          <ol className="picks-review-list">{slate.games.slice().sort((a, b) => a.displayOrder - b.displayOrder).map(game => {
            const row = draft[game.gameId];
            const winner = row?.teamId === game.homeTeamId ? game.homeTeam : row?.teamId === game.awayTeamId ? game.awayTeam : 'No winner selected';
            return <li key={game.gameId}><strong>{row?.rank ? `${row.rank} pts` : 'No rank'}</strong> {game.awayTeam} @ {game.homeTeam}: {winner}</li>;
          })}</ol>
          {!checks?.ready && <div className="notice notice-error" role="alert">
            <strong>This sheet is not ready to submit.</strong>
            <ul>{checks?.missingWinners.map(game => <li key={`winner-${game.gameId}`}>Select a winner for {game.awayTeam} @ {game.homeTeam}.</li>)}
              {checks?.incomplete.map(game => <li key={`partial-${game.gameId}`}>Complete or clear {game.awayTeam} @ {game.homeTeam}.</li>)}
              {checks?.duplicateRanks.map(rank => <li key={`duplicate-${rank}`}>Rank {rank} is assigned more than once.</li>)}
              {checks && checks.invalidRanks.length > 0 && <li>Use confidence ranks 1 through 10.</li>}
              {checks && slate.games.length !== 10 && <li>A complete college pick sheet must contain ten games.</li>}
              {checks && checks.missingRanks.length > 0 && <li>Assign each rank once. Missing: {checks.missingRanks.join(', ')}.</li>}
            </ul>
          </div>}
          {isDirty && <p className="notice notice-error" role="alert">Save this draft before submitting.</p>}
          {!sheet && <p className="notice notice-error" role="alert">Save a draft before submitting.</p>}
          <div className="picks-actions">
            <button className="button button-secondary" type="button" disabled={submitting} onClick={() => setReviewOpen(false)}>Back to picks</button>
            <button className="button button-primary" type="button" disabled={!checks?.ready || isDirty || !sheet || sheet.status !== 'Draft' || locked || submitting || saving || conflict} onClick={() => void handleSubmit()}>{submitting ? 'Submitting…' : 'Confirm submission'}</button>
          </div>
        </section>
      </div>}
    </section>
  );
}
