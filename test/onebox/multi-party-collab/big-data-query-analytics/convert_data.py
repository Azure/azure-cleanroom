import argparse
import sys
from pathlib import Path

import pandas as pd


def main():
    parser = argparse.ArgumentParser(
        description="Converting CSV data to the defined format"
    )
    parser.add_argument("--data-dir", type=Path, required=True, help="Input directory")
    parser.add_argument(
        "--output-dir", type=Path, required=True, help="Output directory"
    )
    parser.add_argument("--format", choices=["json", "parquet"], required=True)
    parser.add_argument(
        "--schema-fields", default="date:date,time:string,author:string,mentions:string"
    )

    args = parser.parse_args()

    columns = [field.split(":")[0] for field in args.schema_fields.split(",")]
    csv_files = list(args.data_dir.rglob("*.csv"))

    if not csv_files:
        print(f"No CSV files found in {args.data_dir}")
        sys.exit(1)

    print(f"Converting CSV to {args.format} format...")

    try:
        for csv_path in csv_files:
            rel_path = csv_path.relative_to(args.data_dir)
            output_file_dir = args.output_dir / rel_path.parent
            output_file_dir.mkdir(parents=True, exist_ok=True)

            print(f"Processing file: {csv_path}")

            df = pd.read_csv(
                csv_path,
                header=None,
                names=columns,
                converters={
                    "date": lambda x: pd.to_datetime(x, format="%d/%m/%Y").date()
                },
            )

            if args.format == "json":
                output_path = output_file_dir / f"{csv_path.stem}.json"
                df.to_json(output_path, orient="records", lines=True, date_format="iso")
                print(f"Successfully converted to JSON with {len(df)} rows.")

            elif args.format == "parquet":
                output_path = output_file_dir / f"{csv_path.stem}.parquet"
                df.to_parquet(output_path, index=False, engine="pyarrow")
                print(f"Successfully converted to Parquet with {len(df)} rows.")

    except Exception as e:
        print(f"An error occurred during the conversion process: {e}", file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
