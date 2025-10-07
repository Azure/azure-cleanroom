import json
import logging
import sys

import requests
import uvicorn
from fastapi import FastAPI, HTTPException
from fastapi.responses import JSONResponse

app = FastAPI()
logging.basicConfig(stream=sys.stdout, level=logging.DEBUG)
logger = logging.getLogger("app")


@app.get("/get_random_quote")
def get_random_quote():
    try:
        response = requests.get("http://api.quotable.io/quotes/random")
        response.raise_for_status()
        quote = json.loads(response.text)
        return JSONResponse(
            status_code=200,
            content={"quote": quote[0]["content"], "author": quote[0]["author"]},
        )
    except ConnectionError as e:
        logger.error(f"Failed to connect to Quotes service: {e}")
        raise HTTPException(
            status_code=500, detail="Failed to connect to Quotes service"
        )
    except requests.HTTPError as e:
        logger.error(f"Failed to connect to Quotes service: {e}")
        raise HTTPException(
            status_code=e.response.status_code,
            detail="Failed to connect to Quotes service",
        )
    except Exception as e:
        logger.error(f"Unexpected error: {e}")
        raise HTTPException(status_code=500, detail="Unexpected error")


@app.get("/search_quotes/{author}")
def search_quotes(author: str):
    try:
        response = requests.get(
            f"http://api.quotable.io/search/quotes?query={author}&fields=author"
        )
        response.raise_for_status()
        authors = json.loads(response.text)
        return JSONResponse(
            status_code=200,
            content={"authors": authors},
        )
    except ConnectionError as e:
        logger.error(f"Failed to connect to Quotes service: {e}")
        raise HTTPException(
            status_code=500, detail="Failed to connect to Quotes service"
        )
    except requests.HTTPError as e:
        logger.error(f"Failed to connect to Quotes service: {e}")
        raise HTTPException(
            status_code=e.response.status_code,
            detail=response.text,
        )
    except Exception as e:
        logger.error(f"Unexpected error: {e}")
        raise HTTPException(status_code=500, detail="Unexpected error")


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
