import glob
import os
import re
import openpyxl
from openpyxl.chart import LineChart, Reference
from openpyxl.styles import Alignment, Font, PatternFill
import pandas as pd

EXCEL_PATH = "TCC_Graficos_Performance_Recursos.xlsx"
BASE_DIR = "reports"  # pasta onde ficam multimodel/ e heterogeneous/

ARCH_MAP = {"multimodel": "Multimodelo", "heterogeneous": "Heterogênea"}
SCEN_MAP = {"low": "Low", "medium": "Medium", "high": "High", "higher": "Higher"}
THEO_RPS = {"Low": 499.0, "Medium": 1000.0, "High": 1999.0, "Higher": 4001.0}
SUBSCENARIOS_ORDER = [
    "write_heavy",
    "read_heavy",
    "graph_heavy",
    "document_heavy",
    "mixed_graph_documents",
]

# Mapeamento fixo das linhas da aba Base_Execucoes (linhas 5 a 28)
ROW_MAP = {}
r = 5
for arch in ["Multimodelo", "Heterogênea"]:
    for scen in ["Low", "Medium", "High", "Higher"]:
        for rep in ["Rep 1", "Rep 2", "Rep 3"]:
            ROW_MAP[(arch, scen, rep)] = r
            r += 1


def clear_old_data(wb):
    # 1. Base_Execucoes: limpa apenas colunas de entrada (linhas 5 a 28)
    ws_base = wb["Base_Execucoes"]
    colunas_entrada = [6, 7, 8, 9, 11, 12] + list(range(14, 26))  # F:I, K:L, N:Y
    for row in range(5, 29):
        for col in colunas_entrada:
            ws_base.cell(row=row, column=col).value = None

    # 2. Detalhes_Subcenarios: limpa registros detalhados (linhas 14+)
    ws_sub = wb["Detalhes_Subcenarios"]
    for row in range(14, max(ws_sub.max_row + 1, 130)):
        for col in range(1, 12):  # A:K
            ws_sub.cell(row=row, column=col).value = None

    # 3. Uso_Recursos_Timeline (Multimodelo): limpa série temporal (linhas 5+)
    ws_tl = wb["Uso_Recursos_Timeline"]
    for row in range(5, max(ws_tl.max_row + 1, 300)):
        for col in range(1, 12):  # A:K
            ws_tl.cell(row=row, column=col).value = None


def load_clean_resource_df(csv_path):
    """Lê resource-usage.csv forçando conversão numérica segura (compatível com pandas 3.x)."""
    df = pd.read_csv(
        csv_path,
        header=None,
        names=["timestamp", "container", "cpu", "mem"],
        dtype=str,
    )
    df["container"] = df["container"].str.strip()
    df["dt"] = pd.to_datetime(df["timestamp"].str.strip(), utc=True, errors="coerce")

    for col in ("cpu", "mem"):
        df[col] = (
            df[col]
            .str.strip()
            .str.replace("%", "", regex=False)
            .str.replace(",", ".", regex=False)
        )
        df[col] = pd.to_numeric(df[col], errors="coerce")

    df = df.dropna(subset=["dt", "container", "cpu", "mem"]).copy()
    return df


def parse_resource_usage(csv_path, warmup_s=30):
    df = load_clean_resource_df(csv_path)
    if df.empty:
        return {}

    df["elapsed_s"] = (df["dt"] - df["dt"].min()).dt.total_seconds()
    active = df[df["elapsed_s"] >= warmup_s].copy()
    if active.empty:
        active = df.copy()

    out = {}

    # 1. Camada de API (ebenezer-api)
    api = active[active["container"] == "ebenezer-api"]
    if not api.empty:
        out["api"] = {
            "cpu_mean": round(float(api["cpu"].mean()), 2),
            "cpu_max": round(float(api["cpu"].max()), 2),
            "mem_mean": round(float(api["mem"].mean()), 2),
            "mem_max": round(float(api["mem"].max()), 2),
        }

    # 2. Camada de Banco de Dados
    dbs = active[active["container"] != "ebenezer-api"].copy()
    db_containers = dbs["container"].unique().tolist()

    if len(db_containers) == 1:
        # Multimodelo: 1 único contêiner (arangodb)
        out["db"] = {
            "cpu_mean": round(float(dbs["cpu"].mean()), 2),
            "cpu_max": round(float(dbs["cpu"].max()), 2),
            "mem_mean": round(float(dbs["mem"].mean()), 2),
            "mem_max": round(float(dbs["mem"].max()), 2),
        }
    elif len(db_containers) >= 2:
        # Heterogênea: soma instantânea de ebenezer-mongodb + ebenezer-neo4j
        mongo = dbs[dbs["container"].str.contains("mongo", case=False)].sort_values("dt")
        neo4j = dbs[dbs["container"].str.contains("neo4j", case=False)].sort_values("dt")

        if not mongo.empty and not neo4j.empty:
            paired = pd.merge_asof(
                mongo[["dt", "cpu", "mem"]].rename(columns={"cpu": "cpu_m", "mem": "mem_m"}),
                neo4j[["dt", "cpu", "mem"]].rename(columns={"cpu": "cpu_n", "mem": "mem_n"}),
                on="dt",
                direction="nearest",
                tolerance=pd.Timedelta(seconds=4),
            ).dropna()

            if not paired.empty:
                cpu_total = paired["cpu_m"] + paired["cpu_n"]
                mem_total = paired["mem_m"] + paired["mem_n"]
                out["db"] = {
                    "cpu_mean": round(float(cpu_total.mean()), 2),
                    "cpu_max": round(float(cpu_total.max()), 2),
                    "mem_mean": round(float(mem_total.mean()), 2),
                    "mem_max": round(float(mem_total.max()), 2),
                }

    return out


def build_mm_timeline_df(csv_path):
    """Pareia séries temporais de ebenezer-api e arangodb em minutos."""
    df = load_clean_resource_df(csv_path)
    if df.empty:
        return pd.DataFrame()

    t0 = df["dt"].min()
    api = df[df["container"] == "ebenezer-api"].sort_values("dt").copy()
    db = df[df["container"] != "ebenezer-api"].sort_values("dt").copy()
    if api.empty or db.empty:
        return pd.DataFrame()

    paired = pd.merge_asof(
        api[["dt", "cpu", "mem"]].rename(columns={"cpu": "api_cpu", "mem": "api_mem"}),
        db[["dt", "cpu", "mem"]].rename(columns={"cpu": "db_cpu", "mem": "db_mem"}),
        on="dt",
        direction="nearest",
        tolerance=pd.Timedelta(seconds=4),
    ).dropna()

    paired["min"] = ((paired["dt"] - t0).dt.total_seconds() / 60.0).round(2)
    return paired[["min", "api_cpu", "db_cpu", "api_mem", "db_mem"]].reset_index(drop=True)


def build_het_timeline_df(csv_path):
    """Pareia séries temporais de ebenezer-api, mongodb e neo4j em minutos."""
    df = load_clean_resource_df(csv_path)
    if df.empty:
        return pd.DataFrame()

    t0 = df["dt"].min()
    api = df[df["container"] == "ebenezer-api"].sort_values("dt").copy()
    mongo = df[df["container"].str.contains("mongo", case=False)].sort_values("dt").copy()
    neo4j = df[df["container"].str.contains("neo4j", case=False)].sort_values("dt").copy()

    if api.empty or mongo.empty or neo4j.empty:
        return pd.DataFrame()

    p1 = pd.merge_asof(
        api[["dt", "cpu", "mem"]].rename(columns={"cpu": "api_cpu", "mem": "api_mem"}),
        mongo[["dt", "cpu", "mem"]].rename(columns={"cpu": "mongo_cpu", "mem": "mongo_mem"}),
        on="dt",
        direction="nearest",
        tolerance=pd.Timedelta(seconds=4),
    )
    paired = pd.merge_asof(
        p1.sort_values("dt"),
        neo4j[["dt", "cpu", "mem"]].rename(columns={"cpu": "neo4j_cpu", "mem": "neo4j_mem"}),
        on="dt",
        direction="nearest",
        tolerance=pd.Timedelta(seconds=4),
    ).dropna()

    paired["min"] = ((paired["dt"] - t0).dt.total_seconds() / 60.0).round(2)
    paired["db_total_cpu"] = (paired["mongo_cpu"] + paired["neo4j_cpu"]).round(2)
    paired["db_total_mem"] = (paired["mongo_mem"] + paired["neo4j_mem"]).round(2)
    return paired[
        [
            "min",
            "api_cpu",
            "mongo_cpu",
            "neo4j_cpu",
            "db_total_cpu",
            "api_mem",
            "mongo_mem",
            "neo4j_mem",
            "db_total_mem",
        ]
    ].reset_index(drop=True)


def create_het_timeline_sheet(wb, het_sources):
    """Cria/atualiza a aba Uso_Recursos_Timeline_Het com dados e gráficos nativos."""
    sheet_name = "Uso_Recursos_Timeline_Het"
    if sheet_name in wb.sheetnames:
        del wb[sheet_name]

    idx_mm = wb.sheetnames.index("Uso_Recursos_Timeline") + 1
    ws = wb.create_sheet(title=sheet_name, index=idx_mm)

    # Estilos consistentes com o restante da planilha
    title_font = Font(name="Calibri", size=14, bold=True, color="FFFFFF")
    title_fill = PatternFill("solid", fgColor="1B365D")
    sub_font = Font(name="Calibri", size=10, italic=True, color="334155")
    hdr_font = Font(name="Calibri", size=10, bold=True, color="FFFFFF")
    hdr_fill = PatternFill("solid", fgColor="2C5282")
    data_font = Font(name="Calibri", size=10, color="0F172A")

    ws.merge_cells("A1:S1")
    ws["A1"] = "TELEMETRIA TEMPORAL DE RECURSOS — ARQUITETURA HETEROGÊNEA (API + MONGODB + NEO4J)"
    ws["A1"].font = title_font
    ws["A1"].fill = title_fill
    ws["A1"].alignment = Alignment(vertical="center", indent=1)
    ws.row_dimensions[1].height = 28

    high_rep = het_sources["High"][0] if "High" in het_sources else "N/D"
    low_rep = het_sources["Low"][0] if "Low" in het_sources else "N/D"
    ws["A2"] = (
        f"Séries temporais coletadas via docker stats para Heterogênea Low ({low_rep}) "
        f"e Heterogênea High ({high_rep})."
    )
    ws["A2"].font = sub_font

    headers = [
        "Amostra",
        "Het_High_Min",
        "Het_High_API_CPU_%",
        "Het_High_Mongo_CPU_%",
        "Het_High_Neo4j_CPU_%",
        "Het_High_DB_Total_CPU_%",
        "Het_High_API_Mem_MB",
        "Het_High_Mongo_Mem_MB",
        "Het_High_Neo4j_Mem_MB",
        "Het_High_DB_Total_Mem_MB",
        "Het_Low_Min",
        "Het_Low_API_CPU_%",
        "Het_Low_Mongo_CPU_%",
        "Het_Low_Neo4j_CPU_%",
        "Het_Low_DB_Total_CPU_%",
        "Het_Low_API_Mem_MB",
        "Het_Low_Mongo_Mem_MB",
        "Het_Low_Neo4j_Mem_MB",
        "Het_Low_DB_Total_Mem_MB",
    ]
    ws.row_dimensions[4].height = 24
    for c_idx, text in enumerate(headers, start=1):
        cell = ws.cell(4, c_idx, text)
        cell.font = hdr_font
        cell.fill = hdr_fill
        cell.alignment = Alignment(horizontal="center", vertical="center", wrap_text=True)

    df_high = build_het_timeline_df(het_sources["High"][1]) if "High" in het_sources else pd.DataFrame()
    df_low = build_het_timeline_df(het_sources["Low"][1]) if "Low" in het_sources else pd.DataFrame()

    max_rows = max(len(df_high), len(df_low))
    for i in range(max_rows):
        r_idx = 5 + i
        c_sample = ws.cell(r_idx, 1, i + 1)
        c_sample.font = data_font
        c_sample.alignment = Alignment(horizontal="center")

        if i < len(df_high):
            row_h = df_high.iloc[i]
            for offset, col_name in enumerate(df_high.columns, start=2):
                c = ws.cell(r_idx, offset, round(float(row_h[col_name]), 2))
                c.font = data_font
                c.number_format = "#,##0.00"

        if i < len(df_low):
            row_l = df_low.iloc[i]
            for offset, col_name in enumerate(df_low.columns, start=11):
                c = ws.cell(r_idx, offset, round(float(row_l[col_name]), 2))
                c.font = data_font
                c.number_format = "#,##0.00"

    # Ajusta largura das colunas
    for col in ws.columns:
        col_letter = openpyxl.utils.get_column_letter(col[0].column)
        ws.column_dimensions[col_letter].width = 16 if col_letter != "A" else 10

    # Adiciona 2 gráficos nativos para Heterogênea High (se houver amostras)
    if not df_high.empty:
        end_row = 4 + len(df_high)
        cats = Reference(ws, min_col=2, min_row=5, max_row=end_row)

        # Gráfico 1: CPU (%) — API, MongoDB, Neo4j e Total Bancos
        ch_cpu = LineChart()
        ch_cpu.title = "Evolução de Uso de CPU (%) — Heterogênea High"
        ch_cpu.style = 10
        ch_cpu.y_axis.title = "CPU (%)"
        ch_cpu.x_axis.title = "Tempo Decorrido (min)"
        ch_cpu.width = 22
        ch_cpu.height = 11
        data_cpu = Reference(ws, min_col=3, max_col=6, min_row=4, max_row=end_row)
        ch_cpu.add_data(data_cpu, titles_from_data=True)
        ch_cpu.set_categories(cats)
        ws.add_chart(ch_cpu, "U4")

        # Gráfico 2: Memória (MB) — API, MongoDB, Neo4j e Total Bancos
        ch_mem = LineChart()
        ch_mem.title = "Evolução de Consumo de Memória (MB) — Heterogênea High"
        ch_mem.style = 10
        ch_mem.y_axis.title = "Memória (MB)"
        ch_mem.x_axis.title = "Tempo Decorrido (min)"
        ch_mem.width = 22
        ch_mem.height = 11
        data_mem = Reference(ws, min_col=7, max_col=10, min_row=4, max_row=end_row)
        ch_mem.add_data(data_mem, titles_from_data=True)
        ch_mem.set_categories(cats)
        ws.add_chart(ch_mem, "U23")


wb = openpyxl.load_workbook(EXCEL_PATH)
clear_old_data(wb)

ws_base = wb["Base_Execucoes"]
ws_sub = wb["Detalhes_Subcenarios"]
ws_tl = wb["Uso_Recursos_Timeline"]

sub_row = 14
mm_timeline_sources = {}
het_timeline_sources = {}

for arch_dir, arch_label in ARCH_MAP.items():
    for scen_prefix, scen_label in SCEN_MAP.items():
        pattern = os.path.join(BASE_DIR, arch_dir, f"{scen_prefix}_all_*")
        folders = sorted(glob.glob(pattern))[-3:]

        for idx, folder in enumerate(folders):
            nb_files = glob.glob(os.path.join(folder, "nbomber_report_*.csv"))
            if not nb_files:
                continue

            rep_label = f"Rep {idx + 1}"
            row_num = ROW_MAP[(arch_label, scen_label, rep_label)]

            nb_file = nb_files[0]
            df_nb = pd.read_csv(nb_file)
            g = df_nb[df_nb["step_name"] == "global information"]
            s = df_nb[df_nb["step_name"] != "global information"]

            h, m, sec = [int(x) for x in str(g["duration"].iloc[0]).split(":")]
            dur_s = h * 3600 + m * 60 + sec

            tp_file = os.path.join(folder, "throughput.csv")
            if os.path.exists(tp_file):
                df_tp = pd.read_csv(tp_file)
                tot = df_tp[df_tp["scenario"] == "TOTAL"].iloc[0]
                theo_rps = float(tot["theoretical_requests_per_second"])
            else:
                theo_rps = THEO_RPS[scen_label]

            actual_rps = round(float(s["ok_rps"].sum()), 2)

            # Grava na aba Base_Execucoes
            ws_base.cell(row_num, 6, os.path.basename(nb_file))
            ws_base.cell(row_num, 7, dur_s)
            ws_base.cell(row_num, 8, theo_rps)
            ws_base.cell(row_num, 9, actual_rps)
            ws_base.cell(row_num, 11, int(g["ok"].sum()))
            ws_base.cell(row_num, 12, int(g["failed"].sum()))
            ws_base.cell(row_num, 14, round(float(s["ok_mean"].mean()), 2))
            ws_base.cell(row_num, 15, round(float(s["ok_50_percent"].mean()), 2))
            ws_base.cell(row_num, 16, round(float(s["ok_95_percent"].mean()), 2))
            ws_base.cell(row_num, 17, round(float(s["ok_99_percent"].mean()), 2))

            # Grava os 5 subcenários na aba Detalhes_Subcenarios
            for sc in SUBSCENARIOS_ORDER:
                g_sc = g[g["scenario"] == sc]
                s_sc = s[s["scenario"] == sc]
                if g_sc.empty or s_sc.empty:
                    continue

                ws_sub.cell(sub_row, 1, arch_label)
                ws_sub.cell(sub_row, 2, scen_label)
                ws_sub.cell(sub_row, 3, rep_label)
                ws_sub.cell(sub_row, 4, sc)
                ws_sub.cell(sub_row, 5, int(g_sc["ok"].sum()))
                ws_sub.cell(sub_row, 6, int(g_sc["failed"].sum()))
                ws_sub.cell(sub_row, 7, round(float(s_sc["ok_rps"].sum()), 2))
                ws_sub.cell(sub_row, 8, round(float(s_sc["ok_mean"].mean()), 2))
                ws_sub.cell(sub_row, 9, round(float(s_sc["ok_50_percent"].mean()), 2))
                ws_sub.cell(sub_row, 10, round(float(s_sc["ok_95_percent"].mean()), 2))
                ws_sub.cell(sub_row, 11, round(float(s_sc["ok_99_percent"].mean()), 2))
                sub_row += 1

            # Processa resource-usage.csv
            res_file = os.path.join(folder, "resource-usage.csv")
            if os.path.exists(res_file):
                res = parse_resource_usage(res_file)
                if "api" in res:
                    ws_base.cell(row_num, 18, res["api"]["cpu_mean"])
                    ws_base.cell(row_num, 19, res["api"]["cpu_max"])
                    ws_base.cell(row_num, 20, res["api"]["mem_mean"])
                    ws_base.cell(row_num, 21, res["api"]["mem_max"])
                if "db" in res:
                    ws_base.cell(row_num, 22, res["db"]["cpu_mean"])
                    ws_base.cell(row_num, 23, res["db"]["cpu_max"])
                    ws_base.cell(row_num, 24, res["db"]["mem_mean"])
                    ws_base.cell(row_num, 25, res["db"]["mem_max"])

                if arch_label == "Multimodelo" and scen_label in ("High", "Low"):
                    mm_timeline_sources[scen_label] = (rep_label, res_file)
                elif arch_label == "Heterogênea" and scen_label in ("High", "Low"):
                    het_timeline_sources[scen_label] = (rep_label, res_file)

            print(f"Atualizado: linha {row_num} ({arch_label} | {scen_label} | {rep_label}) -> {os.path.basename(nb_file)}")

# Atualiza dinamicamente o intervalo das fórmulas AVERAGEIFS em Detalhes_Subcenarios (linhas 6 a 10)
ultima_linha_sub = max(sub_row - 1, 14)
for r_idx in range(6, 11):
    for c_idx in range(2, 8):
        val = ws_sub.cell(r_idx, c_idx).value
        if isinstance(val, str) and val.startswith("="):
            ws_sub.cell(r_idx, c_idx).value = re.sub(
                r"\$14:\$([A-Z]+)\$\d+", rf"$14:$ ${ultima_linha_sub}", val
            )

# Preenche a aba Uso_Recursos_Timeline (Multimodelo High e Low)
df_tl_high = (
    build_mm_timeline_df(mm_timeline_sources["High"][1])
    if "High" in mm_timeline_sources
    else pd.DataFrame()
)
df_tl_low = (
    build_mm_timeline_df(mm_timeline_sources["Low"][1])
    if "Low" in mm_timeline_sources
    else pd.DataFrame()
)
max_tl = max(len(df_tl_high), len(df_tl_low))

for i in range(max_tl):
    r_tl = 5 + i
    ws_tl.cell(r_tl, 1, i + 1)
    if i < len(df_tl_high):
        ws_tl.cell(r_tl, 2, float(df_tl_high.loc[i, "min"]))
        ws_tl.cell(r_tl, 3, round(float(df_tl_high.loc[i, "api_cpu"]), 2))
        ws_tl.cell(r_tl, 4, round(float(df_tl_high.loc[i, "db_cpu"]), 2))
        ws_tl.cell(r_tl, 5, round(float(df_tl_high.loc[i, "api_mem"]), 2))
        ws_tl.cell(r_tl, 6, round(float(df_tl_high.loc[i, "db_mem"]), 2))
    if i < len(df_tl_low):
        ws_tl.cell(r_tl, 7, float(df_tl_low.loc[i, "min"]))
        ws_tl.cell(r_tl, 8, round(float(df_tl_low.loc[i, "api_cpu"]), 2))
        ws_tl.cell(r_tl, 9, round(float(df_tl_low.loc[i, "db_cpu"]), 2))
        ws_tl.cell(r_tl, 10, round(float(df_tl_low.loc[i, "api_mem"]), 2))
        ws_tl.cell(r_tl, 11, round(float(df_tl_low.loc[i, "db_mem"]), 2))

# Cria e preenche a aba Uso_Recursos_Timeline_Het (Heterogênea High e Low)
create_het_timeline_sheet(wb, het_timeline_sources)

wb.save(EXCEL_PATH)
print(
    f"Planilha atualizada com sucesso! "
    f"({ultima_linha_sub - 13} registros de subcenários + abas de timeline Multimodelo e Heterogênea)"
)