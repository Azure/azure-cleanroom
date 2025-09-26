package main

import (
	"bytes"
	"database/sql"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"

	b64 "encoding/base64"

	_ "github.com/lib/pq"
	"github.com/pkg/errors"
)

const (
	db_port         = 5432
	governance_port = 8300
	secrets_port    = 9300
)

func main() {

	fmt.Println("Now starting the application...")

	// Fetch the db config secret from CGS.
	dbConfig, err := GetDbConfig()
	if err != nil {
		panic(err)
	}

	// Fetch the password secret via SKR.
	password, err := GetDbPassword(&dbConfig.Password)
	if err != nil {
		panic(err)
	}

	// Open connection to the database.
	psqlInfo := fmt.Sprintf("host=%s port=%d user=%s password=%s dbname=%s sslmode=disable",
		dbConfig.Ip,
		db_port,
		dbConfig.User,
		password,
		dbConfig.Name)
	db, err := sql.Open("postgres", psqlInfo)
	if err != nil {
		panic(err)
	}
	defer db.Close()

	err = db.Ping()
	if err != nil {
		panic(err)
	}

	fmt.Println("Successfully connected to DB")

	outputLocation := os.Getenv("OUTPUT_LOCATION")
	outputFile, err := os.Create(outputLocation + "/output.txt")
	if err != nil {
		panic(err)
	}
	defer outputFile.Close()

	fmt.Println("Executing query")
	rows, err := db.Query("SELECT id, first_name FROM users WHERE gender=$1", "Female")
	if err != nil {
		panic(err)
	}
	defer rows.Close()
	var rowCount int
	for rows.Next() {
		rowCount++
		var id int
		var firstName string
		err = rows.Scan(&id, &firstName)
		if err != nil {
			panic(err)
		}
		_, err := outputFile.WriteString(fmt.Sprintf("id: %d firstName: %s\n", id, firstName))
		if err != nil {
			panic(err)
		}
	}

	err = rows.Err()
	if err != nil {
		panic(err)
	}

	fmt.Printf("%d rows as output written to output.txt\n", rowCount)
}

type DbConfig struct {
	Endpoint string              `json:"dbEndpoint"`
	Ip       string              `json:"dbIP"`
	User     string              `json:"dbUser"`
	Name     string              `json:"dbName"`
	Password WrappedSecretConfig `json:"dbPassword"`
}

type WrappedSecretConfig struct {
	ClientId    string    `json:"clientId"`
	TenantId    string    `json:"tenantId"`
	Kid         string    `json:"kid"`
	AkvEndpoint string    `json:"akvEndpoint"`
	Kek         KekConfig `json:"kek"`
}

type KekConfig struct {
	Kid         string `json:"kid"`
	AkvEndpoint string `json:"akvEndpoint"`
	MaaEndpoint string `json:"maaEndpoint"`
}

type CgsSecretResponse struct {
	Value string `json:"value"`
}

type UnwrapSecretRequest struct {
	ClientId    string  `json:"clientId"`
	TenantId    string  `json:"tenantId"`
	Kid         string  `json:"kid"`
	AkvEndpoint string  `json:"akvEndpoint"`
	Kek         KekInfo `json:"kek"`
}

type KekInfo struct {
	Kid         string `json:"kid"`
	AkvEndpoint string `json:"akvEndpoint"`
	MaaEndpoint string `json:"maaEndpoint"`
}

type UnwrapSecretResponse struct {
	Value string `json:"value"`
}

func GetDbConfig() (*DbConfig, error) {
	governanceEndpoint := fmt.Sprintf("http://localhost:%d", governance_port)
	dbConfigSecretId := os.Getenv("DB_CONFIG_CGS_SECRET_ID")
	uri := governanceEndpoint + fmt.Sprintf("/secrets/%s", dbConfigSecretId)

	req, err := http.NewRequest("POST", uri, nil)
	if err != nil {
		return nil, errors.Wrapf(err, "NewRequest creation failed")
	}

	client := &http.Client{}
	httpResponse, err := httpClientDoRequest(client, req)
	if err != nil {
		return nil, errors.Wrapf(err, "Get secrets failed")
	}

	httpResponseBodyBytes, err := httpResponseBody(httpResponse)
	if err != nil {
		return nil, errors.Wrapf(err, "Get secrets HTTP response failed")
	}

	secretValue := CgsSecretResponse{}
	err = json.Unmarshal(httpResponseBodyBytes, &secretValue)
	if err != nil {
		return nil, errors.Wrapf(err, "unmarshalling response failed")
	}

	bytes, err := b64.StdEncoding.DecodeString(secretValue.Value)
	if err != nil {
		return nil, errors.Wrapf(err, "b64 decoding of secret value failed")
	}

	dbConfig := DbConfig{}
	err = json.Unmarshal(bytes, &dbConfig)
	if err != nil {
		return nil, errors.Wrapf(err, "unmarshalling to DbConfig failed")
	}

	fmt.Println("Successfully retrieved DB config")
	return &dbConfig, nil
}

func GetDbPassword(passwordConfig *WrappedSecretConfig) (string, error) {
	baseURL := fmt.Sprintf("%s:%d", "http://localhost", secrets_port)
	uri := baseURL + "/secrets/unwrap"
	body, err := json.Marshal(UnwrapSecretRequest{
		ClientId:    passwordConfig.ClientId,
		TenantId:    passwordConfig.TenantId,
		Kid:         passwordConfig.Kid,
		AkvEndpoint: passwordConfig.AkvEndpoint,
		Kek: KekInfo{
			Kid:         passwordConfig.Kek.Kid,
			AkvEndpoint: passwordConfig.Kek.AkvEndpoint,
			MaaEndpoint: passwordConfig.Kek.MaaEndpoint,
		},
	})
	if err != nil {
		return "", errors.Wrapf(err, "Could not marshal secrets/unwrap request body")
	}

	bodyReader := bytes.NewReader(body)
	req, err := http.NewRequest(http.MethodPost, uri, bodyReader)
	if err != nil {
		return "", errors.Wrapf(err, "HTTP post request creation failed")
	}

	req.Header.Set("Content-Type", "application/json")

	client := &http.Client{}
	httpResponse, err := httpClientDoRequest(client, req)
	if err != nil {
		return "", errors.Wrapf(err, "HTTP post secrets/unwrap failed")
	}

	httpResponseBodyBytes, err := httpResponseBody(httpResponse)
	if err != nil {
		return "", errors.Wrapf(err, "pulling HTTP secrets/unwrap response failed")
	}

	response := UnwrapSecretResponse{}
	err = json.Unmarshal(httpResponseBodyBytes, &response)
	if err != nil {
		return "", errors.Wrapf(err, "unmarshalling secrets/unwrap response failed")
	}

	bytes, err := b64.StdEncoding.DecodeString(response.Value)
	if err != nil {
		return "", errors.Wrapf(err, "b64 decoding of secret value failed")
	}

	return string(bytes), nil
}

func httpResponseBody(httpResponse *http.Response) ([]byte, error) {
	defer httpResponse.Body.Close()
	httpResponseBodyBytes, err := io.ReadAll(httpResponse.Body)
	if err != nil {
		return nil, errors.Wrapf(err, "reading HTTP response body failed")
	}

	if httpResponse.StatusCode != 200 {
		return nil, errors.Errorf("code: %d, body: %s", httpResponse.StatusCode, string(httpResponseBodyBytes))
	}

	return httpResponseBodyBytes, nil
}

func httpClientDoRequest(client *http.Client, req *http.Request) (*http.Response, error) {

	resp, err := client.Do(req)

	if err != nil {
		return nil, errors.Wrapf(err, "HTTP request failed")
	}

	return resp, nil
}
