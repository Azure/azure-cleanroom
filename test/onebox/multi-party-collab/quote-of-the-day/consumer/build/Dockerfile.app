FROM mcr.microsoft.com/mirror/docker/library/python:3.11

RUN apt-get update -y && \
    apt-get -y --no-install-recommends install curl

COPY src/*.py .
COPY src/requirements.txt .
RUN pip install -r requirements.txt