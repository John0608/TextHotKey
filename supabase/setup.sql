-- TextHotKey 테스트(베타) 프로그램용 Supabase 설정
-- Supabase 대시보드 → SQL Editor 에 붙여넣고 Run.
--
-- 흐름:
--   1) 앱이 기기 참가 코드(TH-XXXXXX)를 생성
--   2) 사용자가 "신청" → beta_requests 에 code/email INSERT (anon 키, RLS로 insert만 허용)
--   3) 오너가 대시보드 Table editor 에서 해당 행 approved 를 true 로 (= 승인)
--   4) 앱이 rpc beta_is_approved(code) 로 승인 여부(boolean)만 조회
--
-- 보안: anon 키는 공개 키지만 RLS로 INSERT만 허용(approved=false 강제).
--        SELECT/UPDATE/DELETE 불가라 이메일 등 신청 내역은 오너(대시보드)만 본다.

-- 1) 테이블
create table if not exists public.beta_requests (
  id          bigint generated always as identity primary key,
  code        text not null,
  email       text,
  name        text,
  approved    boolean not null default false,
  created_at  timestamptz not null default now()
);

create index if not exists beta_requests_code_idx on public.beta_requests (code);

-- 2) RLS
alter table public.beta_requests enable row level security;

-- 익명(anon) 키: approved=false 인 신청만 INSERT 가능(자가 승인 방지).
--                SELECT/UPDATE/DELETE 정책 없음 → 신청 내역 조회/수정 불가.
drop policy if exists "anon can submit requests" on public.beta_requests;
create policy "anon can submit requests"
  on public.beta_requests
  for insert to anon
  with check (approved = false);

-- 3) 승인 확인 함수 (SECURITY DEFINER = RLS 우회, boolean 만 반환하므로 내역 노출 없음)
create or replace function public.beta_is_approved(p_code text)
returns boolean
language sql
security definer
set search_path = public
as $$
  select exists (
    select 1 from public.beta_requests
    where code = p_code and approved = true
  );
$$;

-- 익명 키가 함수를 실행할 수 있게 허용
grant execute on function public.beta_is_approved(text) to anon;
