-- 迁移后处理：把库里遗留的 Windows 绝对路径改写成容器内路径
-- 旧值示例：C:\Users\lumin\.baihua\vaults\local\中医\鼻渊通
-- 新值：    /vaults/local/中医/鼻渊通
-- （容器内 vaults PV 挂在 /vaults，正文目录已符号链接到 C:\Users\lumin\.baihua\vaults）
\echo '--- 改写前：Vaults.Path 抽样 ---'
select "Name", "Path" from "Vaults" order by "Name" limit 5;

DO $$
DECLARE r record; n bigint;
BEGIN
  FOR r IN
    SELECT table_name, column_name
    FROM information_schema.columns
    WHERE table_schema = 'public'
      AND data_type IN ('text', 'character varying')
  LOOP
    EXECUTE format(
      'UPDATE %I SET %I = replace(replace(%I, %L, %L), %L, %L) WHERE %I LIKE %L',
      r.table_name, r.column_name, r.column_name,
      'C:\Users\lumin\.baihua\vaults', '/vaults',
      E'\\', '/',
      r.column_name, '%C:\Users\lumin\.baihua\vaults%'
    );
    GET DIAGNOSTICS n = ROW_COUNT;
    IF n > 0 THEN
      RAISE NOTICE '改写 %.% : % 行', r.table_name, r.column_name, n;
    END IF;
  END LOOP;
END $$;

\echo '--- 改写后：Vaults.Path ---'
select "Name", "Path" from "Vaults" order by "Name";
\echo '--- 是否还有残留 Windows 路径（应为 0 行）---'
DO $$
DECLARE r record; n bigint; total bigint := 0;
BEGIN
  FOR r IN
    SELECT table_name, column_name FROM information_schema.columns
    WHERE table_schema='public' AND data_type IN ('text','character varying')
  LOOP
    EXECUTE format('SELECT count(*) FROM %I WHERE %I LIKE %L', r.table_name, r.column_name, '%C:\%') INTO n;
    IF n > 0 THEN
      RAISE NOTICE '仍有 Windows 路径: %.% = % 行', r.table_name, r.column_name, n;
      total := total + n;
    END IF;
  END LOOP;
  RAISE NOTICE '剩余 Windows 路径合计: % 行', total;
END $$;
