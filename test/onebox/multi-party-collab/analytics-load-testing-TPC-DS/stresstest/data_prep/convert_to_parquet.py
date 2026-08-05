#!/usr/bin/env python3
"""Convert TPC-DS CSV files to Parquet format using schema definitions from tpcds.sql."""

from __future__ import annotations

import sys as _sys
from pathlib import Path as _Path

_sys.path.insert(0, str(_Path(__file__).resolve().parent.parent / "lib"))

import argparse
import re
import time
from pathlib import Path

import pyarrow as pa
import pyarrow.csv as pcsv
import pyarrow.parquet as pq
from tpcds_helpers import parse_tpcds_create_tables


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Convert TPC-DS CSV files to Parquet.")
    parser.add_argument(
        "--publisher-csv-dir",
        required=True,
        help="Directory containing publisher CSV files.",
    )
    parser.add_argument(
        "--consumer-csv-dir",
        required=True,
        help="Directory containing consumer CSV files.",
    )
    parser.add_argument(
        "--publisher-parquet-dir",
        required=True,
        help="Output directory for publisher Parquet files.",
    )
    parser.add_argument(
        "--consumer-parquet-dir",
        required=True,
        help="Output directory for consumer Parquet files.",
    )
    parser.add_argument(
        "--schema-sql",
        required=True,
        help="Path to tpcds.sql for column type definitions.",
    )
    parser.add_argument(
        "--separator",
        default=",",
        help="CSV field separator (default: comma).",
    )
    return parser.parse_args()


def map_sql_type_to_arrow(raw_type: str) -> pa.DataType:
    """Map a raw SQL type string to a PyArrow data type."""
    data_type = raw_type.lower().strip()
    # decimal(p,s) -> float64 so the Parquet physical type matches the
    # "number" -> DoubleType schema used by dataset_loader.
    if re.match(r"decimal\(\d+\s*,\s*\d+\)", data_type):
        return pa.float64()
    if data_type in {"integer", "bigint"}:
        return pa.int64()
    if data_type == "date":
        return pa.date32()
    # Everything else (char(N), varchar(N), unknown) -> string.
    return pa.string()


def parse_tpcds_schemas(
    schema_sql_path: str,
) -> dict[str, list[tuple[str, pa.DataType]]]:
    """Parse tpcds.sql and return {table: [(col, arrow_type), ...]}."""
    raw = parse_tpcds_create_tables(schema_sql_path)
    return {
        table: [(col, map_sql_type_to_arrow(sql_type)) for col, sql_type in columns]
        for table, columns in raw.items()
    }


def convert_csv_to_parquet(
    csv_path: str,
    parquet_path: str,
    columns: list[tuple[str, pa.DataType]],
    separator: str,
) -> int:
    col_names = [name for name, _ in columns]
    col_types = dict(columns)

    convert_options = pcsv.ConvertOptions(
        column_types=col_types,
        strings_can_be_null=True,
    )
    read_options = pcsv.ReadOptions(
        column_names=col_names,
        autogenerate_column_names=False,
        block_size=256 * 1024 * 1024,
    )
    parse_options = pcsv.ParseOptions(delimiter=separator, quote_char='"')

    # Use streaming reader to avoid loading entire file into memory.
    reader = pcsv.open_csv(
        csv_path,
        read_options=read_options,
        parse_options=parse_options,
        convert_options=convert_options,
    )

    schema = pa.schema(columns)
    total_rows = 0
    with pq.ParquetWriter(parquet_path, schema, compression="snappy") as writer:
        for batch in reader:
            writer.write_batch(batch)
            total_rows += batch.num_rows

    return total_rows


def process_directory(
    csv_dir: str,
    parquet_dir: str,
    schemas: dict[str, list[tuple[str, pa.DataType]]],
    role: str,
    separator: str,
) -> None:
    csv_dir_path = Path(csv_dir)
    parquet_dir_path = Path(parquet_dir)
    parquet_dir_path.mkdir(parents=True, exist_ok=True)

    csv_files = sorted(csv_dir_path.glob("*.csv"))
    if not csv_files:
        print(f"  No CSV files found in {csv_dir}")
        return

    for csv_file in csv_files:
        table_name = csv_file.stem
        if table_name not in schemas:
            print(f"  Warning: no schema for {table_name}, skipping.")
            continue

        parquet_file = parquet_dir_path / f"{table_name}.parquet"
        start = time.time()
        row_count = convert_csv_to_parquet(
            str(csv_file),
            str(parquet_file),
            schemas[table_name],
            separator,
        )
        elapsed = time.time() - start
        csv_size_mb = csv_file.stat().st_size / (1024 * 1024)
        parquet_size_mb = parquet_file.stat().st_size / (1024 * 1024)
        print(
            f"  [{role}] {table_name}: {row_count} rows, "
            f"CSV={csv_size_mb:.1f}MB -> Parquet={parquet_size_mb:.1f}MB "
            f"({elapsed:.1f}s)"
        )


def main() -> None:
    args = parse_args()

    print("Parsing TPC-DS schema definitions...")
    schemas = parse_tpcds_schemas(args.schema_sql)
    print(f"Found schemas for {len(schemas)} tables.")

    parties = [
        ("publisher", args.publisher_csv_dir, args.publisher_parquet_dir),
        ("consumer", args.consumer_csv_dir, args.consumer_parquet_dir),
    ]
    for role, csv_dir, parquet_dir in parties:
        print(f"\nConverting {role} CSV -> Parquet...")
        process_directory(csv_dir, parquet_dir, schemas, role, args.separator)

    print("\nParquet conversion complete.")


if __name__ == "__main__":
    main()
