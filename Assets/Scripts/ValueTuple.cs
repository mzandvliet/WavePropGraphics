/*
Todo: with most recent DOTNET version in Unity, do we still need these?
*/

public struct ValueTuple<T> where T : struct {
    public T a;
    public T b;

    public ValueTuple(T a, T b) {
        this.a = a;
        this.b = b;
    }
}

public struct ValueTuple<T, Y> 
    where T : struct
    where Y : struct {
    public T a;
    public Y b;

    public ValueTuple(T a, Y b) {
        this.a = a;
        this.b = b;
    }
}