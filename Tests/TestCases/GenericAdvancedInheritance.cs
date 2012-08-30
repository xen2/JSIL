using System;
using System.Collections.Generic;

public interface I {}

public interface I<T> : I
{
    void Serialize(T obj);
}

public class A<T>
{
    public virtual void Serialize(ref T obj) {
        Console.WriteLine("A");
    }
}

public class B<T> : A<List<T>>
{
    public override void Serialize(ref List<T> obj) {
        Console.WriteLine("B");
    }
}

public static class Program
{
    public static void Main(string[] args)
    {
        A<List<int>> test = new B<int>();
        List<int> data = null;
        test.Serialize(ref data);
    }
}