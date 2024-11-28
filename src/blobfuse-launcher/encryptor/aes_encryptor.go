package main

import "C"
import (
	"crypto/aes"
	"crypto/cipher"
	"crypto/rand"
	"encoding/json"
	"log"
)

type EncryptChunkArgs struct {
	Data []byte
	Key  []byte
}

type Error struct {
	Code    int
	Message string
}

type EncryptChunkReturn struct {
	CipherText []byte
	Nonce      []byte
	Error      *Error
}

type DecryptedChunk struct {
	PlainText []byte
	Error     *Error
}

type DecryptChunkArgs struct {
	CipherText []byte
	Nonce      []byte
	Key        []byte
}

// EncryptChunk encrypts a chunk of data using AES-GCM
// and returns the nonce along with encrypted chunk with
// authentication tag appended.
func EncryptChunk(data []byte, key []byte) (cipherText []byte, nonce []byte, err error) {
	// Create AES cipher block
	block, err := aes.NewCipher(key)
	if err != nil {
		return nil, nil, err
	}

	// Create AES-GCM cipher
	gcm, err := cipher.NewGCM(block)
	if err != nil {
		return nil, nil, err
	}

	// Generate a random nonce
	nonce = make([]byte, gcm.NonceSize())
	// Generate a random nonce for each chunk
	if _, err := rand.Read(nonce); err != nil {
		return nil, nil, err
	}
	cipherText = gcm.Seal(nil, nonce, data, nil)

	return cipherText, nonce, nil
}

// DecryptChunk decrypts a chunk of data using the provided nonce
// and key and returns the plain text data.
func DecryptChunk(cipherText []byte, nonce []byte, key []byte) (plainText []byte, err error) {
	// Create AES cipher block
	block, err := aes.NewCipher(key)
	if err != nil {
		return nil, err
	}

	// Create AES-GCM cipher
	gcm, err := cipher.NewGCM(block)
	if err != nil {
		return nil, err
	}

	plainText, err = gcm.Open(nil, nonce, cipherText, nil)
	if err != nil {
		return nil, err
	}

	return plainText, nil
}

//export GoEncryptChunk
func GoEncryptChunk(requestJsonPtr *C.char) *C.char {
	documentString := C.GoString(requestJsonPtr)
	var jsonDocument EncryptChunkArgs
	err := json.Unmarshal([]byte(documentString), &jsonDocument)
	if err != nil {
		log.Fatal(err)
	}

	cipherText, nonce, err := EncryptChunk(jsonDocument.Data, jsonDocument.Key)

	response := EncryptChunkReturn{
		CipherText: cipherText,
		Nonce:      nonce,
	}

	if err != nil {
		response.Error = &Error{
			Code:    1,
			Message: err.Error(),
		}
	}

	responseDocument, err := json.Marshal(response)
	if err != nil {
		log.Fatal(err)
	}

	return C.CString(string(responseDocument))
}

//export GoDecryptChunk
func GoDecryptChunk(requestJsonPtr *C.char) *C.char {
	documentString := C.GoString(requestJsonPtr)
	var jsonDocument DecryptChunkArgs
	err := json.Unmarshal([]byte(documentString), &jsonDocument)
	if err != nil {
		log.Fatal(err)
	}

	plainText, err := DecryptChunk(jsonDocument.CipherText, jsonDocument.Nonce, jsonDocument.Key)

	response := DecryptedChunk{
		PlainText: plainText,
	}

	if err != nil {
		response.Error = &Error{
			Code:    1,
			Message: err.Error(),
		}
	}

	responseDocument, err := json.Marshal(response)
	if err != nil {
		log.Fatal(err)
	}

	return C.CString(string(responseDocument))
}

func main() {}
