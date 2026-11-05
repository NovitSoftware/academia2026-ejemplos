FROM python:3.12-slim

ENV PYTHONUNBUFFERED=1 PIP_NO_CACHE_DIR=1 \
    GRABACIONES=/srv/grabaciones CACHE=/srv/datos PORT=5400
WORKDIR /srv/app

COPY app/requirements.txt .
RUN pip install -r requirements.txt \
 && mkdir -p /srv/datos /srv/grabaciones && chmod 777 /srv/datos /srv/grabaciones

COPY app /srv/app

EXPOSE 5400
CMD ["sh", "-c", "uvicorn main:app --host 0.0.0.0 --port ${PORT}"]
