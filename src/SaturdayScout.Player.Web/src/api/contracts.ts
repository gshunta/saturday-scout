// Read DTOs mirror SaturdayScout.Application/ScoutContracts.cs. The current
// OpenAPI document contains routes and request schemas but omits read response
// schemas, so these stay explicit until that contract is enriched.
import type { components, paths } from './openapi';

export type GeneratedApiPaths = paths;
export type GeneratedSavePicksRequest = components['schemas']['SavePicksRequest'];

export interface LeagueDto {
  id: string;
  name: string;
  commissionerUserId: string;
}

export interface PlayerSlateGameDto {
  displayOrder: number;
  gameId: string;
  homeTeamId: string;
  homeTeam: string;
  homeAbbreviation: string;
  awayTeamId: string;
  awayTeam: string;
  awayAbbreviation: string;
  kickoffUtc: string;
  status: string;
  homeScore: number | null;
  awayScore: number | null;
}

export interface PlayerSlateDto {
  id: string;
  leagueId: string;
  season: number;
  week: number;
  status: string;
  lockUtc: string | null;
  publishedUtc: string | null;
  games: PlayerSlateGameDto[];
}

export interface PickDto {
  gameId: string;
  selectedTeamId: string;
  confidenceRank: number;
}

export interface PickSheetDto {
  id: string;
  slateId: string;
  status: 'Draft' | 'Submitted' | string;
  submittedUtc: string | null;
  version: number;
  picks: PickDto[];
}

export interface StandingDto {
  userId: string;
  displayName: string;
  points: number;
  correctPicks: number;
  pendingGames: number;
}

export interface LeaderboardDto {
  leagueId: string;
  scope: string;
  season: number;
  week: number | null;
  entries: StandingDto[];
}

export interface PlayerSessionDto {
  userId: string;
  displayName: string | null;
  roles: string[];
}
