package apiserverproxy

import (
	"fmt"

	corev1 "k8s.io/api/core/v1"
	"k8s.io/apimachinery/pkg/runtime"
	"k8s.io/apimachinery/pkg/runtime/serializer/protobuf"
)

// nodeScheme is the runtime scheme with corev1 types registered.
var nodeScheme = func() *runtime.Scheme {
	s := runtime.NewScheme()
	_ = corev1.AddToScheme(s)
	return s
}()

// nodeProtobufSerializer decodes Kubernetes protobuf-encoded objects.
var nodeProtobufSerializer = protobuf.NewSerializer(nodeScheme, nodeScheme)

// decodeNodeProtobuf decodes a protobuf-encoded Node object.
func decodeNodeProtobuf(data []byte) (*corev1.Node, error) {
	obj, _, err := nodeProtobufSerializer.Decode(data, nil, &corev1.Node{})
	if err != nil {
		return nil, fmt.Errorf("protobuf decode: %w", err)
	}

	node, ok := obj.(*corev1.Node)
	if !ok {
		return nil, fmt.Errorf("decoded object is %T, not *corev1.Node", obj)
	}

	return node, nil
}
