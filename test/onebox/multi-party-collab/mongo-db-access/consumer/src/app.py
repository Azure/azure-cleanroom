import logging
import sys

import uvicorn
from fastapi import FastAPI, HTTPException
from fastapi.responses import JSONResponse
from pymongo import MongoClient
from pymongo.errors import ConnectionFailure
from utilities import *

app = FastAPI()
logging.basicConfig(stream=sys.stdout, level=logging.DEBUG)
logger = logging.getLogger("app")


class RunQueryRequest(BaseModel):
    db_config_secret_name: str


class LeakDataRequest(RunQueryRequest):
    leak_db_config_secret_name: str


def run_db_query(secret_name: str):
    dbConfig = get_db_config(secret_name)
    dbPassword = get_db_password(dbConfig)
    url = f"mongodb://{dbConfig.dbUser}:{dbPassword}@{dbConfig.dbIP}"
    try:
        # Connect to MongoDB.
        client = MongoClient(url, 27017)
        client.admin.command("ping")

        # Create a new database and collection
        db = client[f"{dbConfig.dbName}"]
        collection = db["sales"]

        # MongoDB query to summarize sales data
        pipeline = [
            {"$unwind": "$items"},
            {
                "$group": {
                    "_id": "$items.name",
                    "total_quantity": {"$sum": "$items.quantity"},
                    "total_sales": {
                        "$sum": {"$multiply": ["$items.price", "$items.quantity"]}
                    },
                }
            },
            {"$sort": {"total_sales": -1}},
            {
                "$project": {
                    "id": {"$toString": "$_id"},
                    "total_quantity": {"$toInt": "$total_quantity"},
                    "total_sales": {"$toDouble": "$total_sales"},
                }
            },
        ]

        # Execute the query
        summary = list(collection.aggregate(pipeline))

        # Print the summary
        for item in summary:
            logger.info(
                f"Item: {item['id']}, Total Quantity: {item['total_quantity']}, Total Sales: {item['total_sales']}"
            )

        # Close the connection
        client.close()

        return summary
    except ConnectionFailure as e:
        logger.error(f"Failed to connect to MongoDB: {e}")
        raise


@app.post("/run_query")
def run_query(runQueryRequest: RunQueryRequest):
    try:
        result = run_db_query(runQueryRequest.db_config_secret_name)
        return JSONResponse(
            status_code=200,
            content={"message": "Query executed successfully.", "data": result},
        )
    except ConnectionFailure as e:
        logger.error(f"Failed to connect to MongoDB: {e}")
        raise HTTPException(status_code=500, detail="Failed to connect to MongoDB")
    except Exception as e:
        message = f"Error executing query: {e}"
        logger.error(message)
        raise HTTPException(status_code=500, detail=message)


def main():
    config = uvicorn.Config(
        app=app,
        host="0.0.0.0",
        port=8080,
        log_level="info",
    )
    _server = uvicorn.Server(config)
    _server.run()


if __name__ == "__main__":
    main()
